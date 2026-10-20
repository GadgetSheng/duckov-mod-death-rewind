using Xunit;

namespace DeathRewind.Tests
{
    public class SmokeTests
    {
        [Fact]
        public void Core_Sources_Are_Linked()
        {
            Assert.NotNull(typeof(DeathRewind.Core.Anchor));
        }
    }
}
