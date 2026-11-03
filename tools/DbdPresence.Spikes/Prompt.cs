namespace DbdPresence.Spikes;

/// <summary>Console questions. A closed input stream ends the run instead of looping.</summary>
internal static class Prompt
{
    public static void WaitForEnter(string message)
    {
        Console.WriteLine();
        Console.Write(message + " [Enter] ");
        _ = ReadLine();
    }

    public static string Text(string question)
    {
        Console.Write(question + " ");
        return ReadLine().Trim();
    }

    /// <summary>Asks until the answer is y, n or s (skip). Returns "y", "n" or "skipped".</summary>
    public static string YesNo(string question)
    {
        while (true)
        {
            Console.Write(question + " [y/n/s=skip] ");
            switch (ReadLine().Trim().ToLowerInvariant())
            {
                case "y":
                    return "y";
                case "n":
                    return "n";
                case "s":
                    return "skipped";
                default:
                    Console.WriteLine("Please type y, n or s.");
                    break;
            }
        }
    }

    private static string ReadLine() =>
        Console.ReadLine() ?? throw new EndOfStreamException("Console input was closed.");
}
