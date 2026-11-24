using System.Windows.Media;

namespace SpotOnFly.Models;

/// <summary>
/// Informations sur la piste en cours de lecture, telles que fournies par Windows (GSMTC).
/// </summary>
public sealed record TrackInfo(
    string Title,
    string? Artist,
    string? Album,
    string AppId,
    ImageSource? Thumbnail)
{
    public bool IsPlaying { get; init; } = true;

    public bool CanSkipPrevious { get; init; } = true;

    public bool CanPlayPause { get; init; } = true;

    public bool CanSkipNext { get; init; } = true;
}
