using System.Text;
using Sallyport.Runtime;
using Sallyport.Windows.Routing;

namespace Sallyport.Client.Tests;

/// <summary>Export's file (Backup): the profiles open with their password and nothing else, the rest reads back as written.</summary>
public sealed class BackupTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    // Not a real profile: text of the shape, with nothing of anyone's in it.
    private const string Text = "client\nremote vpn.example 1194 udp\n<ca>\nnot a certificate\n</ca>\n";
    private static readonly StoredProfile Jp = new("ad50a53b0da826ad", "jp", ["udp/1194"], At, null);

    [Fact]
    public void Profiles_open_with_their_password_and_not_with_another()
    {
        var sealedProfiles = Backup.Seal([(Jp, Text)], "correct horse");

        var opened = Backup.Open(sealedProfiles, "correct horse");

        Assert.Equal(Text, Assert.Single(opened!).Text);
        Assert.Null(Backup.Open(sealedProfiles, "wrong horse"));
        // Its own text is not in the file in the clear.
        Assert.DoesNotContain("vpn.example", Encoding.UTF8.GetString(Backup.Write(sealedProfiles, null, null)));
    }

    [Fact]
    public void Profiles_exported_without_a_password_open_without_one()
    {
        var plain = Backup.Seal([(Jp, Text)], null);

        Assert.False(plain.Sealed);
        Assert.Equal(Text, Assert.Single(Backup.Open(plain, "")!).Text);
        Assert.False(Backup.Read(Backup.Write(plain, null, null)).Profiles!.Sealed);
    }

    [Fact]
    public void A_sealed_profile_moved_onto_another_name_does_not_open()
    {
        var sealedProfiles = Backup.Seal([(Jp, Text)], "correct horse");
        var moved = sealedProfiles with { Items = [sealedProfiles.Items[0] with { Name = "us" }] };

        Assert.Null(Backup.Open(moved, "correct horse"));
    }

    [Fact]
    public void Apps_and_connection_settings_read_back_as_written()
    {
        var app = new RoutedItem("0123456789abcdef", "Zenless", @"E:\Games\ZZZ", At, null, LossMode.Hold, 30, [], Uses: [new AppUse(Jp.Id, false)]);
        var connection = new ConnectionPart(new PolicyOverride(RoundsMax: 5, Protocol: "tcp"), new Dictionary<string, PolicyOverride> { [Jp.Id] = new(PingRestartSeconds: 20) },
            new AppDefaults(Jp.Id, LossMode.Direct));

        var file = Backup.Read(Backup.Write(null, [app], connection));

        var back = Assert.Single(file.Apps!);
        Assert.Equal(("Zenless", 30, new AppUse(Jp.Id, false)), (back.Name, back.DirectAfterSeconds, Assert.Single(back.Sections)));
        Assert.Equal(connection.Connection, file.Connection!.Connection);
        Assert.Equal(connection.Defaults, file.Connection.Defaults);
        Assert.Equal(new PolicyOverride(PingRestartSeconds: 20), file.Connection.Policies[Jp.Id]);
        Assert.Null(file.Profiles);
    }

    [Theory]
    [InlineData("not json at all", "it is not a Sallyport export")]
    [InlineData("""{"format":"something-else","version":1}""", "it is not a Sallyport export")]
    [InlineData("""{"format":"sallyport-export","version":99,"exported":"2026-10-20T09:00:00+00:00"}""", "it was written by a newer Sallyport (version 99)")]
    [InlineData("""{"format":"sallyport-export","version":1,"exported":"2026-10-20T09:00:00+00:00"}""", "it holds nothing to import")]
    public void What_is_not_an_export_says_why(string content, string reason) =>
        Assert.Equal(reason, Assert.Throws<InvalidDataException>(() => Backup.Read(Encoding.UTF8.GetBytes(content))).Message);

    [Fact]
    public void A_file_sealed_too_weakly_is_refused_rather_than_opened()
    {
        var sealedProfiles = Backup.Seal([(Jp, Text)], "correct horse") with { Iterations = 1000 };

        Assert.Throws<InvalidDataException>(() => Backup.Open(sealedProfiles, "correct horse"));
    }
}
