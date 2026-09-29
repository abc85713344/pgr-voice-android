namespace PgrVoice.AndroidApp;

/// <summary>A one-shot preview request belongs to the capture grant and physical display seen at request time.</summary>
public sealed class PendingScreenPreview<T> where T : class
{
    private T? request;
    private long session;
    private ScreenDisplayState display = ScreenDisplayState.Unavailable;

    public void Set(long sessionId, ScreenDisplayState displayState, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        session = sessionId;
        display = displayState;
        request = value;
    }

    public bool Matches(long sessionId, ScreenDisplayState displayState) =>
        request != null && session == sessionId && display == displayState;

    public T? Take(long sessionId, ScreenDisplayState displayState)
    {
        var result = Matches(sessionId, displayState) ? request : null;
        Clear();
        return result;
    }

    public void Clear()
    {
        request = null;
        session = 0;
        display = ScreenDisplayState.Unavailable;
    }
}
