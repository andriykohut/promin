using Educast.Master.Core.Setup;

namespace Educast.Master.Core.Tests.Setup;

/// <summary>The "Enter its code" dialog: a stock password box, checked on the action (fix round 4).</summary>
public sealed class EnterCodeViewModelTests
{
    [Fact]
    public void Empty_NoHint_ActionDisabled()
    {
        var vm = new EnterCodeViewModel();

        Assert.Equal(string.Empty, vm.CodeText);
        Assert.False(vm.ShowsHint);
        Assert.False(vm.CanPair);
        Assert.Null(vm.Code);
    }

    [Theory]
    [InlineData("482913")]
    [InlineData("482 913")]
    [InlineData("482-913")]
    [InlineData(" 482913 ")]
    public void AValidCode_NoHint_ActionEnabled_AndTheCodeIsTheSixDigits(string typed)
    {
        var vm = new EnterCodeViewModel { CodeText = typed };

        Assert.False(vm.ShowsHint);
        Assert.True(vm.CanPair);
        Assert.Equal("482913", vm.Code);
        Assert.Equal(typed, vm.CodeText);
    }

    [Theory]
    [InlineData("48291")]
    [InlineData("4829134")]
    [InlineData("48a913")]
    [InlineData("abc")]
    [InlineData("111")]
    public void AnInvalidCode_ShowsTheHint_ActionDisabled_AndThereIsNoCode(string typed)
    {
        var vm = new EnterCodeViewModel { CodeText = typed };

        Assert.True(vm.ShowsHint);
        Assert.False(vm.CanPair);
        Assert.Null(vm.Code);
    }

    [Fact]
    public void InvalidToValidAndBack_FlipsTheHintAndTheAction_WithNotifications()
    {
        var vm = new EnterCodeViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.CodeText = "111";
        Assert.True(vm.ShowsHint);
        Assert.False(vm.CanPair);

        vm.CodeText = "111222";
        Assert.False(vm.ShowsHint);
        Assert.True(vm.CanPair);

        vm.CodeText = "1112223";
        Assert.True(vm.ShowsHint);
        Assert.False(vm.CanPair);

        Assert.Equal(3, raised.Count(n => n == nameof(EnterCodeViewModel.CodeText)));
        Assert.Equal(3, raised.Count(n => n == nameof(EnterCodeViewModel.ShowsHint)));
        Assert.Equal(3, raised.Count(n => n == nameof(EnterCodeViewModel.CanPair)));
    }

    [Fact]
    public void TheHint_IsTheDesignsWording() => Assert.Equal("The code is 6 digits", EnterCodeViewModel.HintText);
}
