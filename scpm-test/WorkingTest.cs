using scpm.working;

namespace scpm_test;

public class WorkingTest
{
    [Fact]
    public async Task TestWorking()
    {
        Assert.True(await Tester.Test());
    }
}
