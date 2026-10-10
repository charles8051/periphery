using System.Xml.Linq;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Periphery.Cli.Tests;

/// <summary>
/// Every description in the command tree is Spectre markup. One unescaped <c>[</c> or
/// <c>]</c> makes <c>periphery --help</c> fail for every command, not just its own.
/// </summary>
public sealed class HelpMarkupTests
{
    private static (int ExitCode, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
        });
        console.Profile.Width = 10_000;

        var app = new CommandApp();
        app.Configure(config =>
        {
            CliApp.Configure(config);
            config.ConfigureConsole(console);
        });
        return (app.Run(args), output.ToString());
    }

    [Fact]
    public void EveryDescriptionParsesAsMarkup()
    {
        var (exitCode, xml) = Run("cli", "xmldoc");
        Assert.Equal(0, exitCode);

        var descriptions = XDocument.Parse(xml).Descendants("Description").Select(d => d.Value).ToList();
        Assert.NotEmpty(descriptions);

        var broken = descriptions
            .Where(d =>
            {
                try { _ = new Markup(d); return false; }
                catch (InvalidOperationException) { return true; }
            })
            .ToList();
        Assert.Empty(broken);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("serial", "--help")]
    [InlineData("devices", "--help")]
    public void HelpRenders(params string[] args)
    {
        var (exitCode, output) = Run(args);

        Assert.Equal(0, exitCode);
        Assert.Contains("USAGE:", output);
    }
}
