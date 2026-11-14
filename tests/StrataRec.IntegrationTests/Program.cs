namespace StrataRec.IntegrationTests;

/// <summary>
/// The test assembly's entry point, in place of the one the test SDK generates (<c>GenerateProgramFile</c> is off). The
/// test host never calls it; a test runs it in a child process when what the test checks could end the process
/// (<see cref="TimerFaultTests"/>).
/// </summary>
internal static class Program
{
    public static int Main(string[] args) =>
        args switch
        {
            ["timer-fault", var site] => TimerFaultChild.Run(site),
            ["cancel-then-dispose"] => TimerFaultChild.CancelThenDispose(),
            _ => 0,
        };
}
