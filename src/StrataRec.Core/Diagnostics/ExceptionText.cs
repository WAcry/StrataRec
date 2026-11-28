namespace StrataRec.Core.Diagnostics;

/// <summary>
/// The text of an exception thrown by code the framework does not own (a client, a connector, an update source), for a
/// diagnostic: its message, or, when reading the message throws, its type.
/// </summary>
public static class ExceptionText
{
    public static string Of(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            return exception.Message;
        }
        catch (Exception)
        {
            return (exception.GetType().FullName ?? exception.GetType().Name) + " (reading its message threw)";
        }
    }
}
