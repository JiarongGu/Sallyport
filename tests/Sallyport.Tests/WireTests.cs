using Sallyport.Protocol;
using Sallyport.Profile;

namespace Sallyport.Tests;

public class ControlPacketTests
{
    private static readonly byte[] Key = TestKeys.StaticKey();

    [Fact]
    public void ARoundTripKeepsEveryField()
    {
        var sent = new ControlPacket(Opcode.ControlV1, 2, [1, 2, 3, 4, 5, 6, 7, 8], 9, 1_700_000_000, [4, 5], [8, 7, 6, 5, 4, 3, 2, 1], 6, [0xaa, 0xbb]);

        var got = ControlPacket.Decode(sent.Encode(TlsCrypt.ForClient(Key)), new TlsCrypt(Key, sendHalf: 0));

        Assert.NotNull(got);
        Assert.Equal(sent.Opcode, got.Opcode);
        Assert.Equal(sent.KeyId, got.KeyId);
        Assert.Equal(sent.SessionId, got.SessionId);
        Assert.Equal(sent.ReplayId, got.ReplayId);
        Assert.Equal(sent.Time, got.Time);
        Assert.Equal(sent.Acks, got.Acks);
        Assert.Equal(sent.RemoteSessionId, got.RemoteSessionId);
        Assert.Equal(sent.MessageId, got.MessageId);
        Assert.Equal(sent.Payload, got.Payload);
    }

    [Fact]
    public void TheFirstByteIsTheOpcodeAboveTheKeyId()
    {
        var packet = new ControlPacket(Opcode.ControlHardResetClientV2, 0, new byte[8], 1, 0, [], null, 0, []).Encode(TlsCrypt.ForClient(Key));
        Assert.Equal(0x38, packet[0]);
    }

    [Fact]
    public void AnAckCarriesNoMessageId()
    {
        var ack = new ControlPacket(Opcode.AckV1, 0, new byte[8], 3, 0, [0], new byte[8], null, []);
        var bytes = ack.Encode(TlsCrypt.ForClient(Key));

        // 17 header + 32 tag + (1 count + 4 ack + 8 peer session), and nothing after.
        Assert.Equal(17 + 32 + 13, bytes.Length);
        var got = ControlPacket.Decode(bytes, new TlsCrypt(Key, sendHalf: 0));
        Assert.NotNull(got);
        Assert.Null(got.MessageId);
        Assert.Equal([0u], got.Acks);
    }

    [Fact]
    public void AnAckWithoutThePeersSessionCannotBeWritten() =>
        Assert.Throws<InvalidOperationException>(() =>
            new ControlPacket(Opcode.AckV1, 0, new byte[8], 1, 0, [1], null, null, []).Encode(TlsCrypt.ForClient(Key)));
}

public class KeyMethod2Tests
{
    [Fact]
    public void AClientMessageRoundTrips()
    {
        var source = KeySource.ForClient();
        var bytes = KeyMethod2Message.Write(source, "V4,dev-type tun", "", "", "IV_PROTO=130\n");

        Assert.True(KeyMethod2Message.TryRead(bytes, fromClient: true, out var got, out var used));
        Assert.Equal(bytes.Length, used);
        Assert.Equal(source.PreMaster, got!.Source.PreMaster);
        Assert.Equal(source.Random1, got.Source.Random1);
        Assert.Equal(source.Random2, got.Source.Random2);
        Assert.Equal("V4,dev-type tun", got.Options);
        Assert.Equal(["", "", "IV_PROTO=130\n"], got.Trailing);
    }

    [Fact]
    public void TheLayoutIsZeroThenTwoThenTheKeySource()
    {
        var bytes = KeyMethod2Message.Write(KeySource.ForServer(), "x");
        Assert.Equal([0, 0, 0, 0, 2], bytes[..5]);
        // 5 + 2 × 32 random bytes, then the string: length 2 (one byte and its NUL), 'x', NUL.
        Assert.Equal([0, 2, (byte)'x', 0], bytes[69..]);
    }

    [Fact]
    public void AnEmptyStringIsALengthOfZero()
    {
        var bytes = KeyMethod2Message.Write(KeySource.ForServer(), "");
        Assert.Equal([0, 0], bytes[69..]);
    }

    [Fact]
    public void AnIncompleteMessageIsNotReadYet()
    {
        var bytes = KeyMethod2Message.Write(KeySource.ForServer(), "V4,dev-type tun");
        Assert.False(KeyMethod2Message.TryRead(bytes.AsSpan(0, bytes.Length - 1), fromClient: false, out _, out _));
        Assert.True(KeyMethod2Message.TryRead(bytes, fromClient: false, out _, out _));
    }

    [Fact]
    public void TextIsNotAKeyMethodMessage()
    {
        var text = new byte[80];
        "AUTH_FAILED"u8.CopyTo(text);
        Assert.Throws<FormatException>(() => KeyMethod2Message.TryRead(text, fromClient: false, out _, out _));
    }
}

public class OvpnProfileTests
{
    [Fact]
    public void ReadsRemotesBlocksAndOptions()
    {
        var key = TestKeys.StaticKey();
        var profile = OvpnProfile.Parse($"""
            client
            proto udp
            remote a.example.test 1194 udp
            remote a.example.test 1194 udp
            remote a.example.test 443 tcp-client
            remote b.example.test
            remote-cert-tls server
            cipher AES-256-GCM
            data-ciphers AES-256-GCM:AES-128-GCM
            auth SHA512
            # auth-user-pass is commented out
            <ca>
            -----BEGIN CERTIFICATE-----
            AAAA
            -----END CERTIFICATE-----
            </ca>
            <tls-crypt>
            {TestKeys.StaticKeyText(key)}
            </tls-crypt>
            """.Replace("\n", "\r\n"));

        Assert.Equal(
            [new Remote("a.example.test", 1194, "udp"), new Remote("a.example.test", 443, "tcp"), new Remote("b.example.test", 1194, "udp")],
            profile.Remotes);
        Assert.Equal(key, profile.TlsCryptKey);
        Assert.Contains("-----BEGIN CERTIFICATE-----", profile.CaPem);
        Assert.True(profile.RemoteCertTlsServer);
        Assert.False(profile.AuthUserPass);
        Assert.Equal("AES-256-GCM", profile.Cipher);
        Assert.Equal(["AES-256-GCM", "AES-128-GCM"], profile.DataCiphers);
        Assert.Equal("SHA512", profile.Auth);
        Assert.Null(profile.CertPem);
    }

    [Fact]
    public void RecognisesWhatItCannotYetDo()
    {
        var profile = OvpnProfile.Parse("remote x 1194\nauth-user-pass\n<tls-crypt-v2>\nAAAA\n</tls-crypt-v2>\n");
        Assert.True(profile.AuthUserPass);
        Assert.True(profile.TlsCryptV2);
        Assert.Null(profile.TlsCryptKey);
    }

    [Fact]
    public void AStaticKeyOfTheWrongSizeIsRefused() =>
        Assert.Throws<FormatException>(() => StaticKey.Parse("-----BEGIN OpenVPN Static key V1-----\n00ff\n-----END OpenVPN Static key V1-----\n"));

    [Theory]
    [InlineData("verify-x509-name server name", "server", "name")]
    [InlineData("verify-x509-name 'C=KG, ST=NA, CN=Server-1'", "C=KG, ST=NA, CN=Server-1", "subject")]
    [InlineData("verify-x509-name \"OpenVPN Server\" name-prefix", "OpenVPN Server", "name-prefix")]
    public void ReadsTheServerNameItPins(string line, string name, string type)
    {
        var profile = OvpnProfile.Parse($"remote x 1194\n{line}\n");
        Assert.Equal(new ServerName(name, type), profile.VerifyName);
        Assert.Null(profile.UnsupportedCheck);
    }

    [Theory]
    [InlineData("verify-x509-name server ou", "verify-x509-name ou")]
    [InlineData("peer-fingerprint AB:CD", "peer-fingerprint")]
    [InlineData("<peer-fingerprint>\nAB:CD\n</peer-fingerprint>", "peer-fingerprint")]
    [InlineData("verify-hash AB:CD SHA256", "verify-hash")]
    [InlineData("tls-verify ./check.sh", "tls-verify")]
    public void AServerCheckItCannotMakeRefusesTheProfile(string line, string check)
    {
        var profile = OvpnProfile.Parse($"remote x 1194\n{line}\n");
        Assert.Equal(check, profile.UnsupportedCheck);
    }

    [Fact]
    public void ADurationPastADayIsADay()
    {
        var policy = OvpnProfile.Parse("remote x 1194\nconnect-timeout 2147483647\nconnect-retry 2147483647\n").Policy;
        Assert.Equal(TimeSpan.FromDays(1), policy.AttemptTimeout);
        Assert.Equal(TimeSpan.FromDays(1), policy.RetryWait);
        Assert.True(policy.WaitAfter(1000) <= TimeSpan.FromDays(1));
    }
}

public class ServerWordsTests
{
    [Fact]
    public void AChallengeAndATokenAreNeverQuoted()
    {
        var said = ServerWords.Quote("AUTH_FAILED,CRV1:R,E:Om01u7Fh4LrGBS7uh0SWmzwabUiGiW6l:Y3Ix:Please enter your OTP");
        Assert.StartsWith("AUTH_FAILED,", said);
        Assert.DoesNotContain("Om01u7", said);
        Assert.DoesNotContain("Y3Ix", said);
    }

    [Fact]
    public void WhatIsQuotedIsPrintableAndShort()
    {
        var said = ServerWords.Quote("AUTH_FAILED\n" + new string('x', 500));
        Assert.Equal(121, said.Length);
        Assert.DoesNotContain('\n', said);
    }

    [Theory]
    [InlineData(" auth-token SECRET")]
    [InlineData("auth-token\tSECRET")]
    [InlineData("AUTH-TOKEN SECRET")]
    public void ATokenIsRedactedHoweverItIsSpaced(string option) =>
        Assert.DoesNotContain("SECRET", PushOptions.Redact(option));
}
