using FightReview;

return await ProgramMain.RunAsync(args);

internal static class ProgramMain
{
    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.Help)
            {
                Options.PrintUsage();
                return Task.FromResult(0);
            }

            var xcai = XcaiLogReader.Read(options.XcaiPath ?? throw new InvalidOperationException("--xcai is required."));
            var incidents = IncidentDetector.Detect(xcai);
            var output = options.OutputDirectory ?? DefaultOutputDirectory(options.XcaiPath!);
            ArtifactWriter.Write(new ReviewBundle(xcai, incidents), output);
            Console.WriteLine($"Wrote agent improvement packet to: {Path.Combine(Path.GetFullPath(output), "agent.improvement.json")}");
            Console.WriteLine($"Detected incidents: {incidents.Count}");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Task.FromResult(1);
        }
    }

    private static string DefaultOutputDirectory(string xcaiPath)
    {
        var fullPath = Path.GetFullPath(xcaiPath);
        var parent = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var name = Path.GetFileNameWithoutExtension(fullPath);
        return Path.Combine(parent, $"{name}-review");
    }

    private sealed record Options(
        string? XcaiPath,
        string? OutputDirectory,
        bool Help)
    {
        public static Options Parse(string[] args)
        {
            string? xcai = null;
            string? output = null;
            var help = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--xcai":
                        xcai = ReadValue(args, ref i, "--xcai");
                        break;
                    case "--out":
                        output = ReadValue(args, ref i, "--out");
                        break;
                    case "-h":
                    case "--help":
                        help = true;
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown argument '{args[i]}'.");
                }
            }

            if (!help)
            {
                if (xcai == null)
                {
                    throw new InvalidOperationException("--xcai is required.");
                }
            }

            return new Options(xcai, output, help);
        }

        public static void PrintUsage()
        {
            Console.WriteLine("""
XCAI Fight Review

Usage:
  dotnet run --project tools/FightReview -- --xcai <xcai.jsonl> [--out <dir>]

Outputs:
  agent.improvement.json
""");
        }

        private static string ReadValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new InvalidOperationException($"{option} requires a value.");
            }

            return args[++index];
        }
    }
}
