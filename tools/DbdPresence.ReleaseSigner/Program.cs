using System.Security.Cryptography;
using System.Text;
using DbdPresence.ReleaseSigner;

// Offline release-signing tool (V2-S6). Usage: docs/owner/release-keys.md and README.txt next to the program.
return Cli.Run(args, Console.Out, Console.Error, new ConsolePassphrasePrompt(), TimeProvider.System);

namespace DbdPresence.ReleaseSigner
{
    /// <summary>Argument handling. Exit codes: 0 done, 1 refused or failed (the message says why), 2 wrong usage.</summary>
    internal static class Cli
    {
        public const string Usage = """
            DBD Presence release signer (offline). Commands:

              keygen release  --kid release-prod-1  --out E:\dbdp-keys
              keygen recovery --kid recovery-prod-1 --out E:\dbdp-keys
                  Makes a key pair. The private key is written encrypted with a passphrase you choose.

              sign-feed --key E:\dbdp-keys\release-prod-1.private.pem --channel stable --set production --packages C:\dbdp-releases --out releases-stable.json
                        [--notes notes.txt] [--previous last-releases-stable.json] [--lifetime-days 30] [--version N]
                  Signs a release feed for the newest three packages in the folder. --set production refuses any package
                  that is not a production build (take it from the Release workflow); --set staging refuses production builds.

              resign --key E:\dbdp-keys\release-prod-1.private.pem --feed releases-stable.json --out releases-stable-2.json
                  Signs the same feed again (a feed is valid for 30 days at most).

              sign-key-replacement --key E:\dbdp-keys\recovery-prod-1.private.pem --revoke release-prod-1
                        --add E:\dbdp-keys\release-prod-2.public.json --previous key-replacement-1.json --out key-replacement-2.json
                  Revokes a compromised key and trusts its successor (recovery key only). Give the last replacement with
                  --previous so its revocations are kept; only the very first replacement uses --first instead.

              verify --document releases-stable.json --trust src\DbdPresence.Online\TrustedServiceKeys.json --set production [--packages C:\dbdp-releases]
                  Checks a document exactly as the app would. With --packages it also checks each listed package's size,
                  SHA-256 and that it is a build for that set.

              check-build --packages installer\vpk --channel stable --set production
                  Checks that every full package in the folder is an app build for that set, without signing anything
                  (the Release workflow runs it on the package it uploads).
            """;

        public static int Run(string[] args, TextWriter output, TextWriter error, IPassphrasePrompt prompt, TimeProvider time)
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                output.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            try
            {
                var options = Options.Parse(args.Skip(1));
                var commands = new Commands(output, prompt, time);
                switch (args[0])
                {
                    case "keygen":
                        var kind = options.Positional switch
                        {
                            ["release"] => OfflineKeyKind.Release,
                            ["recovery"] => OfflineKeyKind.Recovery,
                            _ => throw new UsageException("keygen needs 'release' or 'recovery'."),
                        };
                        commands.KeyGen(kind, options.Required("kid"), options.Required("out"));
                        return 0;
                    case "sign-feed":
                        commands.SignFeed(
                            options.Required("key"), options.Optional("kid"), options.Required("channel"), options.Required("set"), options.Required("packages"), options.Required("out"),
                            options.Optional("notes"), options.Optional("previous"), options.OptionalLong("version"), options.OptionalDays("lifetime-days"));
                        return 0;
                    case "resign":
                        commands.Resign(options.Required("key"), options.Optional("kid"), options.Required("feed"), options.Required("out"), options.OptionalDays("lifetime-days"));
                        return 0;
                    case "sign-key-replacement":
                        commands.SignKeyReplacement(options.Required("key"), options.Optional("kid"), options.All("revoke"), options.All("add"), options.Optional("previous"), options.Required("out"), options.Flag("first"));
                        return 0;
                    case "check-build":
                        return commands.CheckBuild(options.Required("packages"), options.Required("channel"), options.Required("set")) ? 0 : 1;
                    case "verify":
                        return commands.Verify(options.Required("document"), options.Required("trust"), options.Required("set"), options.Optional("packages")) ? 0 : 1;
                    default:
                        throw new UsageException($"Unknown command '{args[0]}'.");
                }
            }
            catch (UsageException ex)
            {
                error.WriteLine(ex.Message);
                error.WriteLine();
                error.WriteLine(Usage);
                return 2;
            }
            catch (Exception ex) when (ex is SignerException or IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
            {
                error.WriteLine("Stopped: " + ex.Message);
                return 1;
            }
        }
    }

    /// <summary>A command line that does not match the usage.</summary>
    internal sealed class UsageException : Exception
    {
        public UsageException()
        {
        }

        public UsageException(string message)
            : base(message)
        {
        }

        public UsageException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>Parsed <c>--name value</c> pairs and positional words.</summary>
    internal sealed class Options
    {
        /// <summary>Options that take no value.</summary>
        private static readonly string[] s_flags = ["first"];
        private readonly List<(string Name, string Value)> _pairs = [];
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        public List<string> Positional { get; } = [];

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            using var e = args.GetEnumerator();
            while (e.MoveNext())
            {
                var arg = e.Current;
                if (arg.StartsWith("--", StringComparison.Ordinal) && s_flags.Contains(arg[2..], StringComparer.Ordinal))
                {
                    options._flags.Add(arg[2..]);
                }
                else if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    if (!e.MoveNext() || e.Current.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new UsageException($"{arg} needs a value.");
                    }

                    options._pairs.Add((arg[2..], e.Current));
                }
                else
                {
                    options.Positional.Add(arg);
                }
            }

            return options;
        }

        public bool Flag(string name) => _flags.Contains(name);

        public string Required(string name) => Optional(name) ?? throw new UsageException($"--{name} is required.");

        public string? Optional(string name)
        {
            var values = All(name);
            return values.Count switch
            {
                0 => null,
                1 => values[0],
                _ => throw new UsageException($"--{name} may be given only once."),
            };
        }

        public IReadOnlyList<string> All(string name) => [.. _pairs.Where(p => p.Name == name).Select(p => p.Value)];

        public long? OptionalLong(string name) =>
            Optional(name) is { } text ? (long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : throw new UsageException($"--{name} must be a whole number.")) : null;

        public TimeSpan? OptionalDays(string name) =>
            OptionalLong(name) is { } days ? (days is >= 1 and <= 30 ? TimeSpan.FromDays(days) : throw new UsageException($"--{name} must be 1 to 30.")) : null;
    }

    /// <summary>Reads a passphrase from the console without echoing it (or one line when input is redirected).</summary>
    internal sealed class ConsolePassphrasePrompt : IPassphrasePrompt
    {
        public string Read(string prompt)
        {
            Console.Error.Write(prompt);
            if (Console.IsInputRedirected)
            {
                return Console.In.ReadLine() ?? string.Empty;
            }

            var text = new StringBuilder();
            for (var key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
            {
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (text.Length > 0)
                    {
                        text.Length--;
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    text.Append(key.KeyChar);
                }
            }

            Console.Error.WriteLine();
            return text.ToString();
        }
    }
}
