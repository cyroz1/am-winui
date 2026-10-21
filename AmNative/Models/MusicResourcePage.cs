namespace AmNative.Models;

public sealed record MusicResourcePage(IReadOnlyList<MusicResource> Items, IReadOnlyList<Uri> NextPages);
