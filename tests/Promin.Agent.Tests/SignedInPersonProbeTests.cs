using System.ComponentModel;

namespace Promin.Agent.Tests;

public class SignedInPersonProbeTests
{
    private const string Placed = @"Received files go to C:\Users\anna\Desktop\Promin, written as PC\anna.";

    /// <summary>
    /// Anna, signed in to an agent that runs as SYSTEM: <see cref="RunningAs"/> is who work runs as, and is Anna only
    /// inside <see cref="RunAsAsync{T}"/>. <see cref="Name"/> throws, so a line that says PC\anna can only have read
    /// it inside the impersonation.
    /// </summary>
    private sealed class FakePerson(Func<PersonFolders?> folders, Func<Task<int?>> helper) : ISignedInPerson
    {
        public IReadOnlyList<string>? HelperArguments { get; private set; }
        public TimeSpan? HelperWait { get; private set; }
        public string RunningAs { get; private set; } = @"NT AUTHORITY\SYSTEM";

        public string? Name => throw new NotSupportedException();

        public PersonFolders? Folders() => folders();

        public async Task<T> RunAsAsync<T>(Func<Task<T>> work)
        {
            RunningAs = @"PC\anna";
            try
            {
                return await work();
            }
            finally
            {
                RunningAs = @"NT AUTHORITY\SYSTEM";
            }
        }

        public Task<int?> RunHelperAsync(IReadOnlyList<string> arguments, TimeSpan wait)
        {
            HelperArguments = arguments;
            HelperWait = wait;
            return helper();
        }
    }

    private static FakePerson Anna(Func<Task<int?>> helper) =>
        new(() => new PersonFolders(@"C:\Users\anna\Desktop", @"C:\Users\anna\AppData\Local"), helper);

    private static FakePerson Anna(int? helperExit) => Anna(() => Task.FromResult(helperExit));

    private static Task<string> Describe(FakePerson person) => SignedInPersonProbe.DescribeAsync(person, () => person.RunningAs);

    [Fact]
    public async Task APersonAndAWorkingHelper_SaysWhereFilesGoAndAsWhom()
    {
        var person = Anna(0);

        var line = await Describe(person);

        Assert.Equal($@"{Placed} The helper starts as PC\anna.", line);
        Assert.Equal(["--helper-check"], person.HelperArguments);
        Assert.Equal(TimeSpan.FromSeconds(10), person.HelperWait);
    }

    [Fact]
    public async Task AHelperThatRunsAsSystem_SaysFilesWillNotBeOpened() =>
        Assert.Equal(
            $"{Placed} The helper started as SYSTEM: received files will not be opened.",
            await Describe(Anna(2)));

    [Fact]
    public async Task AHelperThatDoesNotFinish_SaysSo() =>
        Assert.Equal(
            $"{Placed} The helper did not finish within 10 seconds.",
            await Describe(Anna(helperExit: null)));

    [Fact]
    public async Task AHelperThatFails_SaysItsExitCode() =>
        Assert.Equal(
            $"{Placed} The helper ended with code 1.",
            await Describe(Anna(1)));

    [Fact]
    public async Task AHelperThatCannotStart_SaysWhy()
    {
        var denied = new Win32Exception(5);

        var line = await Describe(Anna(() => throw denied));

        Assert.Equal($"{Placed} The helper cannot be started: {denied.Message}", line);
    }

    [Fact]
    public async Task NobodySignedIn_SaysFilesCannotBePlaced() =>
        Assert.Equal(
            "Nobody is signed in: received files cannot be placed.",
            await Describe(new FakePerson(() => null, () => throw new InvalidOperationException())));

    [Fact]
    public async Task FoldersThatCannotBeFound_SayWhy() =>
        Assert.Equal(
            "Received files cannot be placed: no profile",
            await Describe(
                new FakePerson(() => throw new Win32Exception(2, "no profile"), () => throw new InvalidOperationException())));
}
