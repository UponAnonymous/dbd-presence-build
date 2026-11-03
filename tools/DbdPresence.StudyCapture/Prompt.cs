namespace DbdPresence.StudyCapture;

/// <summary>Console prompts. Input is only read from this console window; the tool never reads keys typed elsewhere.</summary>
internal static class Prompt
{
    public static bool Confirm(string question)
    {
        Console.Write(question);
        var answer = Console.ReadLine();
        return answer is not null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    public static void WaitForEnter(string message)
    {
        Console.WriteLine(message);
        Console.ReadLine();
    }

    /// <summary>Whether Q was pressed in this console window since the last call.</summary>
    public static bool QPressed()
    {
        if (Console.IsInputRedirected)
        {
            return false;
        }

        while (Console.KeyAvailable)
        {
            if (Console.ReadKey(intercept: true).Key == ConsoleKey.Q)
            {
                return true;
            }
        }

        return false;
    }
}
