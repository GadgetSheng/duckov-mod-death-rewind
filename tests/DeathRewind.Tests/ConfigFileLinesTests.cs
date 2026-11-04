using System.Collections.Generic;
using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class ConfigFileLinesTests
    {
        [Fact]
        public void Upsert_ReplacesExistingKey()
        {
            var lines = new List<string> { "enabled=true", "maxRewinds=1", "anchorCount=16" };

            ConfigFileLines.Upsert(lines, "maxRewinds", "3");

            Assert.Equal("maxRewinds=3", lines[1]);
            Assert.Equal(3, lines.Count);
        }

        [Fact]
        public void Upsert_AppendsWhenMissing()
        {
            var lines = new List<string> { "enabled=true" };

            ConfigFileLines.Upsert(lines, "rewindCooldown", "90");

            Assert.Equal("rewindCooldown=90", lines[lines.Count - 1]);
            Assert.Equal(2, lines.Count);
        }

        [Fact]
        public void Upsert_KeepsCommentsAndOtherKeys()
        {
            var lines = new List<string> { "# 注释", "enabled=true", "", "# another" };

            ConfigFileLines.Upsert(lines, "maxRewinds", "2");

            Assert.Equal("# 注释", lines[0]);
            Assert.Equal("enabled=true", lines[1]);
            Assert.Equal("", lines[2]);
            Assert.Equal("# another", lines[3]);
            Assert.Equal("maxRewinds=2", lines[4]);
        }

        [Fact]
        public void Upsert_IgnoresCaseAndSurroundingSpaces()
        {
            var lines = new List<string> { "  MaxRewinds = 1  " };

            ConfigFileLines.Upsert(lines, "maxRewinds", "3");

            Assert.Single(lines);
            Assert.Equal("maxRewinds=3", lines[0]);
        }

        [Fact]
        public void Upsert_IgnoresLinesWithoutEquals()
        {
            var lines = new List<string> { "garbage", "enabled=true" };

            ConfigFileLines.Upsert(lines, "maxRewinds", "3");

            Assert.Equal("garbage", lines[0]);
            Assert.Equal("enabled=true", lines[1]);
            Assert.Equal("maxRewinds=3", lines[2]);   // 追加到末尾
        }
    }
}
