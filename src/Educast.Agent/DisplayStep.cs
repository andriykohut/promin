namespace Educast.Agent;

/// <summary>A step of drawing or presenting a picture failed. The message names the step, so a log line says which call refused.</summary>
internal sealed class DisplayStepException(string step, Exception inner)
    : Exception($"{step} failed: {inner.GetType().Name}: {inner.Message}", inner)
{
    public string Step { get; } = step;

    /// <summary>Runs <paramref name="action"/>; whatever it throws comes out as a <see cref="DisplayStepException"/> naming <paramref name="step"/>.</summary>
    public static void Run(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not DisplayStepException)
        {
            throw new DisplayStepException(step, e);
        }
    }

    public static T Run<T>(string step, Func<T> function)
    {
        try
        {
            return function();
        }
        catch (Exception e) when (e is not DisplayStepException)
        {
            throw new DisplayStepException(step, e);
        }
    }
}
