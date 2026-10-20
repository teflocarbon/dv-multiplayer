namespace Multiplayer.Core.Trains;

/// <summary>
/// Spans (metres along a track from its start) across the joint between two tracks, as vanilla's
/// bogies cross from one to the next (<c>Bogie.UpdatePointSetTraveller</c>): past a track's end the
/// bogie goes on along the track joined there, at that track's start if the joint is its
/// <c>first</c> end (<c>Junction.Branch.first</c>), else at its end, running backwards.
/// </summary>
public static class TrackSpans
{
    /// <summary>
    /// Where a span past one end of a track (below 0: past its start; <paramref name="length"/> or
    /// more: past its end) is on the track joined at that end. False when it is past that track too.
    /// </summary>
    public static bool TryCross(double span, double length, bool first, double nextLength, out double nextSpan)
    {
        double past = span < 0 ? -span : span - length;
        nextSpan = first ? past : nextLength - past;
        return past >= 0 && nextSpan >= 0 && nextSpan < nextLength;
    }

    /// <summary>
    /// The reverse of <see cref="TryCross"/>: a span on the track joined at this track's end
    /// (<paramref name="atEnd"/>) or start, as a span along this one, extended past that end.
    /// </summary>
    public static double FromNext(double nextSpan, double length, bool atEnd, bool first, double nextLength)
    {
        double past = first ? nextSpan : nextLength - nextSpan;
        return atEnd ? length + past : -past;
    }
}
