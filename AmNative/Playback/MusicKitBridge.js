(() => {
  const status = document.getElementById("status");
  const signInButton = document.getElementById("sign-in");
  const startButton = document.getElementById("start");
  const cancelButton = document.getElementById("cancel");
  const dismissVideoButton = document.getElementById("dismiss-video");
  let music = null;
  let isReady = false;
  let previousItemId = "";
  let commandChain = Promise.resolve();

  function post(message) {
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage(message);
    }
  }

  function setStatus(message) {
    status.textContent = message;
  }

  function getNowPlaying() {
    const item = music?.player?.nowPlayingItem;
    return item ? { id: item.id ?? "", type: item.type ?? "" } : { id: "", type: "" };
  }

  function isMusicVideoType(type) {
    return String(type ?? "").toLowerCase().replace(/[^a-z]/g, "").includes("musicvideo");
  }

  function publishState() {
    if (!music?.player) return;
    const player = music.player;
    const item = player.nowPlayingItem;
    const type = item?.type ?? "";
    const id = item?.id ?? "";
    if (id !== previousItemId) {
      previousItemId = id;
    }
    const isVideo = isMusicVideoType(type);
    document.body.classList.toggle("playing-video", isVideo && Boolean(player.isPlaying));
    post({
      type: "state",
      isPlaying: Boolean(player.isPlaying),
      positionSeconds: Number.isFinite(player.currentPlaybackTime) ? player.currentPlaybackTime : 0,
      durationSeconds: Number.isFinite(player.currentPlaybackDuration) ? player.currentPlaybackDuration : 0,
      volume: Number.isFinite(player.volume) ? player.volume : 0.5,
      shuffle: Boolean(player.shuffle),
      repeatMode: Number(player.repeatMode ?? 0),
      queuePosition: Number(player.queue?.position ?? -1),
      isVideo,
      ...getNowPlaying()
    });
  }

  function attachEvents() {
    const events = MusicKit.Events;
    [
      events.playbackStateDidChange,
      events.playbackTimeDidChange,
      events.mediaItemDidChange,
      events.queuePositionDidChange,
      events.queueItemsDidChange,
      events.playbackVolumeDidChange
    ].filter(Boolean).forEach(eventName => music.addEventListener(eventName, publishState));

    if (events.mediaPlaybackError) {
      music.addEventListener(events.mediaPlaybackError, event => {
        const detail = event?.detail;
        const message = detail?.message ?? detail?.error?.message ?? String(detail ?? "Apple Music could not play this item.");
        post({ type: "playbackError", message });
      });
    }
  }

  async function configure(developerToken) {
    if (!window.MusicKit) throw new Error("MusicKit on the Web did not load. Check your internet connection.");
    await MusicKit.configure({
      developerToken,
      app: { name: "AM Native", build: "0.1.0" },
      suppressErrorDialog: true
    });
    music = MusicKit.getInstance();
    attachEvents();
    isReady = true;
    setStatus(music.isAuthorized ? "Apple Music is authorized." : "Sign in to authorize full-track playback.");
    return {
      isAuthorized: Boolean(music.isAuthorized),
      userToken: music.isAuthorized ? music.musicUserToken ?? null : null
    };
  }

  async function authorizeFromUserGesture() {
    if (!music) throw new Error("Apple Music is not ready yet.");
    signInButton.disabled = true;
    setStatus("Waiting for Apple Music authorization…");
    try {
      const userToken = await music.authorize();
      signInButton.hidden = true;
      startButton.hidden = true;
      cancelButton.hidden = true;
      setStatus("Apple Music is authorized. Starting playback…");
      post({ type: "authorized", userToken });
    } catch (error) {
      signInButton.disabled = false;
      setStatus(error?.message ?? "Apple Music authorization was not completed.");
      post({ type: "authorizationError", message: error?.message ?? "Apple Music authorization was not completed." });
    }
  }

  async function addSegment(descriptor, initialize) {
    if (descriptor.kind === "url") {
      return initialize
        ? music.setQueue({ url: descriptor.url })
        : music.playLater({ url: descriptor.url });
    }

    const ids = descriptor.ids;
    const options = descriptor.kind === "musicVideos"
      ? { musicVideos: ids }
      : { songs: ids };
    return initialize ? music.setQueue(options) : music.playLater(options);
  }

  function makeSegments(items) {
    const segments = [];
    for (const item of items) {
      const next = item.kind === "url"
        ? { kind: "url", url: item.url }
        : { kind: item.kind, ids: [item.id] };

      const previous = segments.at(-1);
      const maxSegmentSize = next.kind === "musicVideos" ? 100 : 300;
      // MusicKit resolves each batch by ID, so repeated IDs must be loaded in separate segments.
      if (next.kind !== "url" && previous?.kind === next.kind && previous.ids.length < maxSegmentSize &&
          !previous.ids.includes(next.ids[0])) {
        previous.ids.push(next.ids[0]);
      } else {
        segments.push(next);
      }
    }

    const chunked = [];
    for (const segment of segments) {
      if (segment.kind === "url") {
        chunked.push(segment);
        continue;
      }
      const chunkSize = segment.kind === "musicVideos" ? 100 : 300;
      for (let index = 0; index < segment.ids.length; index += chunkSize) {
        chunked.push({ kind: segment.kind, ids: segment.ids.slice(index, index + chunkSize) });
      }
    }
    return chunked;
  }

  async function queueAndPlay(payload) {
    if (!isReady || !music) throw new Error("Apple Music player is not initialized.");
    if (!music.isAuthorized) throw new Error("Sign in to Apple Music before starting full-track playback.");
    const items = payload.items ?? [];
    if (items.length === 0) throw new Error("There are no Apple Music tracks to play.");

    const segments = makeSegments(items);
    for (let index = 0; index < segments.length; index++) {
      await addSegment(segments[index], index === 0);
    }

    music.player.volume = payload.volume;
    music.player.repeatMode = payload.repeat ? 2 : 0;
    music.player.shuffle = false;
    await music.player.changeToMediaAtIndex(payload.startIndex ?? 0);
    if (Number.isFinite(payload.positionSeconds) && payload.positionSeconds > 0) {
      await music.player.seekToTime(payload.positionSeconds);
    }
    music.player.shuffle = Boolean(payload.shuffle);
    if (payload.isPlaying === false) {
      await music.player.pause();
    } else {
      await music.player.play();
    }
    startButton.hidden = true;
    signInButton.hidden = true;
    cancelButton.hidden = true;
    setStatus(payload.isPlaying === false ? "Apple Music is paused." : "Apple Music is playing.");
    publishState();
  }

  async function replaceQueue(payload) {
    if (!isReady || !music?.isAuthorized) throw new Error("Sign in to Apple Music before changing the full-track queue.");
    const items = payload.items ?? [];
    if (items.length === 0) {
      await music.player.stop();
      return;
    }

    const segments = makeSegments(items);
    for (let index = 0; index < segments.length; index++) {
      await addSegment(segments[index], index === 0);
    }

    music.player.volume = payload.volume;
    music.player.repeatMode = payload.repeat ? 2 : 0;
    music.player.shuffle = false;
    const startIndex = Math.max(0, Math.min(Number(payload.startIndex) || 0, items.length - 1));
    await music.player.changeToMediaAtIndex(startIndex);
    if (Number.isFinite(payload.positionSeconds) && payload.positionSeconds > 0) {
      await music.player.seekToTime(payload.positionSeconds);
    }
    music.player.shuffle = Boolean(payload.shuffle);
    if (payload.isPlaying) {
      await music.player.play();
    } else {
      await music.player.pause();
    }
    publishState();
  }

  async function command(message) {
    const { commandId, action, payload = {} } = message;
    let result = {};
    try {
      switch (action) {
        case "configure":
          result = await configure(payload.developerToken);
          break;
        case "showAuthorization":
          signInButton.hidden = false;
          signInButton.disabled = false;
          startButton.hidden = true;
          cancelButton.hidden = false;
          setStatus("Sign in to Apple Music to authorize full-track playback.");
          break;
        case "showStart":
          signInButton.hidden = true;
          if (music?.player?.queue?.length > 0) {
            startButton.hidden = false;
            cancelButton.hidden = false;
            setStatus("Apple Music is ready. Select Start playback to begin.");
          } else {
            startButton.hidden = true;
            cancelButton.hidden = true;
            setStatus("Apple Music playback is authorized.");
            post({ type: "authorizationReady" });
          }
          break;
        case "playQueue":
          await queueAndPlay(payload);
          break;
        case "appendQueue": {
          if (!music?.isAuthorized) throw new Error("Sign in to Apple Music before adding full tracks.");
          const segments = makeSegments(payload.items ?? []);
          for (const segment of segments) await addSegment(segment, false);
          break;
        }
        case "replaceQueue":
          await replaceQueue(payload);
          break;
        case "pause":
          await music.player.pause();
          break;
        case "resume":
          await music.player.play();
          break;
        case "seek":
          await music.player.seekToTime(payload.positionSeconds);
          break;
        case "next":
          await music.player.skipToNextItem();
          break;
        case "previous":
          await music.player.skipToPreviousItem();
          break;
        case "setVolume":
          music.player.volume = payload.volume;
          break;
        case "setShuffle":
          music.player.shuffle = Boolean(payload.enabled);
          break;
        case "setRepeat":
          music.player.repeatMode = payload.enabled ? 2 : 0;
          break;
        case "stop":
          await music.player.stop();
          await music.player.clearQueue();
          break;
        default:
          throw new Error(`Unsupported MusicKit command: ${action}`);
      }
      publishState();
      post({ type: "commandResult", commandId, success: true, result });
    } catch (error) {
      const messageText = error?.message ?? "Apple Music could not complete the playback request.";
      const gestureRequired = (action === "playQueue" || action === "replaceQueue" || action === "resume") &&
        /notallowed|user gesture|user activation/i.test(messageText);
      if (gestureRequired) {
        signInButton.hidden = true;
        startButton.hidden = false;
        startButton.disabled = false;
        cancelButton.hidden = false;
        setStatus("Select Start playback to allow Apple Music to begin streaming.");
        post({ type: "userGestureRequired" });
      }
      post({ type: "commandResult", commandId, success: false, error: messageText });
    }
  }

  window.amNativeReceive = command;
  signInButton.addEventListener("click", authorizeFromUserGesture);
  startButton.addEventListener("click", async () => {
    startButton.disabled = true;
    try {
      await music.player.play();
      startButton.hidden = true;
      cancelButton.hidden = true;
      setStatus("Apple Music is playing.");
      post({ type: "started" });
      publishState();
    } catch (error) {
      startButton.disabled = false;
      setStatus(error?.message ?? "Apple Music could not start playback.");
      post({ type: "playbackError", message: error?.message ?? "Apple Music could not start playback." });
    }
  });
  cancelButton.addEventListener("click", () => post({ type: "cancel" }));
  dismissVideoButton.addEventListener("click", () => post({ type: "hideVideo" }));

  window.chrome?.webview?.addEventListener("message", event => {
    if (event.data && typeof event.data === "object") {
      const message = event.data;
      commandChain = commandChain.then(
        () => command(message),
        () => command(message));
    }
  });

  const announceMusicKitReady = () => post({ type: "bridgeReady" });
  if (window.MusicKit) {
    announceMusicKitReady();
  } else {
    document.addEventListener("musickitloaded", announceMusicKitReady, { once: true });
  }
  setTimeout(() => {
    if (!window.MusicKit) post({ type: "bridgeError", message: "MusicKit on the Web did not load. Check your internet connection." });
  }, 45000);
})();
