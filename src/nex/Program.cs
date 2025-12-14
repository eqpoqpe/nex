using static Nex.Commands.RunCommandBuilder;

var rootCommand = new RootCommand("A fantastic tool for .NET");

// nex run --example [name] -- [args...]
var runCommand = CreateRunCommand();

rootCommand.Subcommands.Add(runCommand);

ParseResult pr = rootCommand.Parse(args);

return await pr.InvokeAsync();
