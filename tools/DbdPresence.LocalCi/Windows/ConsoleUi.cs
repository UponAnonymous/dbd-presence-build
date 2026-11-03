using System.Text;

namespace DbdPresence.LocalCi.Windows;

/// <summary>The console as setup and the menu use it. The token is read without showing it.</summary>
internal sealed class ConsoleUi : IConsoleUi
{
    public void WriteLine(string text) => Console.WriteLine(text);

    public string? ReadLine() => Console.ReadLine();

    public string? ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var typed = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }

    public bool Confirm(string question)
    {
        Console.Write(question + " ");
        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
