using Insolvia.Core.Local;
using Insolvia.Core.Memory;

namespace Insolvia.Core.Tests;

public sealed class LocalSubjectsTests
{
    [Fact]
    public void A_subject_matches_the_vector_local_auth_pins()
    {
        // backend/local-auth/server.test.ts pins the same value: a drift between
        // the two languages would sign a seeded person in as a stranger.
        Assert.Equal("a035a66c-67ed-5d1c-bf27-3a8b3405d7a3", LocalSubjects.For("dev@insolvia.test"));
    }

    [Fact]
    public void Addresses_are_case_insensitive_as_the_pool_treats_them()
    {
        Assert.Equal(LocalSubjects.For("dev@insolvia.test"), LocalSubjects.For(" Dev@Insolvia.TEST "));
    }

    [Fact]
    public async Task The_memory_directory_uses_the_subject_it_is_given()
    {
        var directory = new MemoryUserDirectory(LocalSubjects.For);

        var subject = await directory.CreateUserAsync("invited@insolvia.test", TestContext.Current.CancellationToken);

        Assert.Equal(LocalSubjects.For("invited@insolvia.test"), subject);
    }

    [Fact]
    public async Task The_memory_directory_mints_a_uuid_by_default()
    {
        var subject = await new MemoryUserDirectory().CreateUserAsync("invited@insolvia.test", TestContext.Current.CancellationToken);

        Assert.NotEqual(LocalSubjects.For("invited@insolvia.test"), subject);
    }
}
