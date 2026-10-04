namespace Misscore;

/// <summary>
/// Decides whether a status frame is worth putting on the debug channel. A frame identical to the
/// last one sent is dropped, so something that is simply sitting there costs nothing, and sends are
/// capped so a fast tick rate cannot flood the channel.
/// <para>
/// Whenever the editor changes what it watches, call <see cref="Invalidate"/>. The newly watched
/// runner has usually been ticking along unwatched and its current frame matches what it last sent,
/// so without the reset the "nothing changed" rule would suppress it indefinitely — the editor
/// clears the old colours, no replacement frame ever arrives, and the graph stays blank.
/// </para>
/// </summary>
public sealed class FrameThrottle {
    byte[] _lastSent;
    ulong _lastSentMsec;

    /// <summary>Forgets what was last sent, so the next frame goes out whatever it contains.</summary>
    public void Invalidate() => _lastSent = null;

    /// <summary>
    /// True when <paramref name="frame"/> should be sent; the frame is then recorded as the last
    /// one sent. False leaves the throttle untouched.
    /// </summary>
    public bool TryTake(byte[] frame, ulong nowMsec, ulong minIntervalMsec) {
        if (frame == null) return false;

        if (_lastSent != null) {
            if (nowMsec - _lastSentMsec < minIntervalMsec) return false;
            if (Same(_lastSent, frame)) return false;
        }

        _lastSent = (byte[]) frame.Clone();
        _lastSentMsec = nowMsec;
        return true;
    }

    static bool Same(byte[] left, byte[] right) {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++) {
            if (left[i] != right[i]) return false;
        }
        return true;
    }
}
