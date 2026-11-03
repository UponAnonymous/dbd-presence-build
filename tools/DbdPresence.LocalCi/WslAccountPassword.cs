using System.Security.Cryptography;

namespace DbdPresence.LocalCi;

/// <summary>The random password of the dedicated Windows account (local CI round 2, red-team F2): 32 characters, never typed by anyone, kept only in the closed data folder under Windows data protection.</summary>
public static class WslAccountPassword
{
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string Symbols = "!#%+-=?@^_";

    /// <summary>A 32-character password with letters of both cases, a digit and a symbol, from the system's random source (so it meets any complexity rule Windows may have set). Letters that look alike (l, I, O) and the digits 0 and 1 are left out.</summary>
    public static string New()
    {
        const int Length = 32;
        const string All = Lower + Upper + Digits + Symbols;
        var characters = new char[Length];
        characters[0] = Pick(Lower);
        characters[1] = Pick(Upper);
        characters[2] = Pick(Digits);
        characters[3] = Pick(Symbols);
        for (var i = 4; i < Length; i++)
        {
            characters[i] = Pick(All);
        }

        RandomNumberGenerator.Shuffle(characters.AsSpan());
        return new string(characters);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
