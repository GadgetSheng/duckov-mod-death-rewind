using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class RewindCooldownTests
    {
        [Fact]
        public void Initially_Ready()
        {
            var cd = new RewindCooldown();

            Assert.True(cd.IsReady(0));
            Assert.Equal(0, cd.Remaining(0));
        }

        [Fact]
        public void Start_MakesNotReady()
        {
            var cd = new RewindCooldown();
            cd.SetDuration(30);

            cd.Start(100);

            Assert.False(cd.IsReady(100));
            Assert.Equal(30, cd.Remaining(100));
        }

        [Fact]
        public void Remaining_CountsDown()
        {
            var cd = new RewindCooldown();
            cd.SetDuration(60);
            cd.Start(100);

            Assert.Equal(60, cd.Remaining(100));
            Assert.Equal(30, cd.Remaining(130));
            Assert.Equal(0, cd.Remaining(160));
        }

        [Fact]
        public void IsReady_BecomesTrueAfterDuration()
        {
            var cd = new RewindCooldown();
            cd.SetDuration(30);
            cd.Start(100);

            Assert.False(cd.IsReady(129.9));
            Assert.True(cd.IsReady(130));
        }

        [Fact]
        public void SetDuration_RecomputesRunningCooldown()
        {
            var cd = new RewindCooldown();
            cd.SetDuration(90);
            cd.Start(0);

            cd.SetDuration(30);

            Assert.Equal(30, cd.Remaining(0));
            Assert.True(cd.IsReady(30));
        }

        [Fact]
        public void Reset_ClearsCooldown()
        {
            var cd = new RewindCooldown();
            cd.SetDuration(60);
            cd.Start(0);

            cd.Reset();

            Assert.True(cd.IsReady(0));
            Assert.Equal(0, cd.Remaining(0));
        }

        [Fact]
        public void Disabled_IsAlwaysReady_AndStartDoesNothing()
        {
            var cd = new RewindCooldown();
            cd.SetDuration(60);
            cd.Enabled = false;

            cd.Start(0);

            Assert.True(cd.IsReady(0));
            Assert.True(cd.IsReady(10));
            Assert.Equal(0, cd.Remaining(0));
        }
    }
}
