using System.ComponentModel;

namespace Promin.Agent.Tests;

public class SignedInPersonProbeTests
{
    private const string Placed = @"Received files go to C:\Users\anna\Desktop\Promin, written as PC\anna.";

    private sealed class FakePerson(Func<PersonFolders?> folders, Func<Task<int?>> helper) : ISignedInPerson
    {
        public IReadOnlyList<string>? HelperArguments { get; private set; }
        public TimeSpan? HelperWait { get; private set; }

        public string? Name => folders() is null ? null : @"PC\anna";

        public PersonFolders? Folders() => folders();

        public Task<T> RunAsAsync<T>(Func<Task<T>> work) => work();

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

    [Fact]
    public async Task APersonAndAWorkingHelper_SaysWhereFilesGoAndAsWhom()
    {
        var person = Anna(0);

        var line = await SignedInPersonProbe.DescribeAsync(person);

        Assert.Equal($@"{Placed} The helper starts as PC\anna.", line);
        Assert.Equal(["--helper-check"], person.HelperArguments);
        Assert.Equal(TimeSpan.FromSeconds(10), person.HelperWait);
    }

    [Fact]
    public async Task AHelperThatRunsAsSystem_SaysFilesWillNotBeOpened() =>
        Assert.Equal(
            $"{Placed} The helper started as SYSTEM: received files will not be opened.",
            await SignedInPersonProbe.DescribeAsync(Anna(2)));

    [Fact]
    public async Task AHelperThatDoesNotFinish_SaysSo() =>
        Assert.Equal(
            $"{Placed} The helper did not finish within 10 seconds.",
            await SignedInPersonProbe.DescribeAsync(Anna(helperExit: null)));

    [Fact]
    public async Task AHelperThatFails_SaysItsExitCode() =>
        Assert.Equal(
            $"{Placed} The helper ended with code 1.",
            await SignedInPersonProbe.DescribeAsync(Anna(1)));

    [Fact]
    public async Task AHelperThatCannotStart_SaysWhy()
    {
        var denied = new Win32Exception(5);

        var line = await SignedInPersonProbe.DescribeAsync(Anna(() => throw denied));

        Assert.Equal($"{Placed} The helper cannot be started: {denied.Message}", line);
    }

    [Fact]
    public async Task NobodySignedIn_SaysFilesCannotBePlaced() =>
        Assert.Equal(
            "Nobody is signed in: received files cannot be placed.",
            await SignedInPersonProbe.DescribeAsync(new FakePerson(() => null, () => throw new InvalidOperationException())));

    [Fact]
    public async Task FoldersThatCannotBeFound_SayWhy() =>
        Assert.Equal(
            "Received files cannot be placed: no profile",
            await SignedInPersonProbe.DescribeAsync(
                new FakePerson(() => throw new Win32Exception(2, "no profile"), () => throw new InvalidOperationException())));
}
