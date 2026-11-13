using System.CommandLine;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Cli.Commands;

/// <summary><c>stratarec operators</c>: the operators, connectors and clients this binary provides (design 10.4).</summary>
internal static class OperatorsCommand
{
    public static Command Create()
    {
        var format = CommonOptions.Format();
        var command = new Command("operators", "List operators (with effects) and other capabilities of this binary.") { format };
        command.SetAction(parse => Run(parse.GetValue(format)!, Console.Out));
        return command;
    }

    internal static int Run(string format, TextWriter output)
    {
        var environment = new PlanEnvironment();
        if (format == "json")
        {
            output.WriteLine(Output.ToJson(json =>
            {
                json.WriteStartObject();
                json.WriteStartArray("operators");
                foreach (var factory in environment.Operators.Factories)
                {
                    var d = factory.Descriptor;
                    json.WriteStartObject();
                    json.WriteString("id", d.Id);
                    json.WriteString("effects", d.Effects.ToString());
                    json.WriteString("description", d.Description);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteStartArray("capabilities");
                foreach (var capability in environment.Capabilities())
                {
                    json.WriteStringValue(capability);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }));
            return 0;
        }

        foreach (var factory in environment.Operators.Factories)
        {
            var d = factory.Descriptor;
            output.WriteLine($"{d.Id,-30} {Effects(d.Effects),-24} {d.Description}");
        }

        output.WriteLine();
        output.WriteLine("capabilities: " + string.Join(", ", environment.Capabilities()));
        output.WriteLine("config schema of an operator: stratarec schema operator <Name@Version>");
        return 0;
    }

    private static string Effects(Effects effects) => effects == Pipeline.Operators.Effects.None ? "-" : effects.ToString().Replace(", ", "|", StringComparison.Ordinal);
}
