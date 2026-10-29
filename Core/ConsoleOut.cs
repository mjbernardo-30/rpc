namespace ReleaseChecker.Core;

public static class ConsoleOut
{
    public static void Write(string text, ConsoleColor color)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = old;
    }

    public static void WriteLine(string text, ConsoleColor color)
    {
        Write(text, color);
        Console.WriteLine();
    }

    public static ConsoleColor StatusColor(string status) => status switch
    {
        "FAIL" => ConsoleColor.Red,
        "PASS" => ConsoleColor.Green,
        _ => ConsoleColor.DarkGray
    };

    /// <summary>Prints a finding remark in its severity colour.</summary>
    public static void Remark(Finding f, string indent = "       ", bool showFile = false)
    {
        var color = f.IsSuppressed ? ConsoleColor.DarkGray : f.Severity switch
        {
            Severity.Error => ConsoleColor.Red,
            Severity.Warning => ConsoleColor.Yellow,
            _ => ConsoleColor.Gray
        };
        string file = showFile && f.File is not null ? $" ({f.File})" : "";
        WriteLine($"{indent}{f.Remark}{file}", color);
    }
}
