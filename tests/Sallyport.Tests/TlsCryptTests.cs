using Sallyport.Crypto;

namespace Sallyport.Tests;

public class TlsCryptTests
{
    [Fact]
    public void AesCtrMatchesTheNistVectors()
    {
        // NIST SP 800-38A, F.5.5 CTR-AES256.Encrypt. Four blocks, so the counter is incremented three times.
        var key = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
        var counter = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var plaintext = Convert.FromHexString(
            "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
        var expected = Convert.FromHexString(
            "601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c52b0930daa23de94ce87017ba2d84988ddfc9c58db67aada613c2dd08457941a6");
        var output = new byte[plaintext.Length];
        AesCtr.Transform(key, counter, plaintext, output);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void TheCounterCarriesAcrossItsWholeWidth()
    {
        var key = new byte[32];
        var allOnes = Enumerable.Repeat((byte)0xff, 16).ToArray();
        var output = new byte[32];
        AesCtr.Transform(key, allOnes, new byte[32], output);
        // The second block's key stream is the all-zero counter: ff..ff + 1 wraps every byte.
        var zeroBlock = new byte[16];
        AesCtr.Transform(key, new byte[16], new byte[16], zeroBlock);
        Assert.Equal(zeroBlock, output[16..]);
    }

    [Fact]
    public void WhatTheClientWrapsTheServerReads()
    {
        var key = TestKeys.StaticKey();
        var client = TlsCrypt.ForClient(key);
        var server = new TlsCrypt(key, sendHalf: 0);
        var header = new byte[TlsCrypt.HeaderLength];
        header[0] = 7 << 3;
        var plaintext = "a control packet body that runs past one AES block"u8.ToArray();

        var packet = client.Wrap(header, plaintext);

        Assert.Equal(TlsCrypt.HeaderLength + TlsCrypt.TagLength + plaintext.Length, packet.Length);
        Assert.Equal(plaintext, server.Unwrap(packet));
        Assert.Equal(plaintext, client.Unwrap(server.Wrap(header, plaintext)));
    }

    [Fact]
    public void ASideCannotReadItsOwnPackets()
    {
        // Each direction has its own keys, so a packet reflected back at its sender is rejected.
        var client = TlsCrypt.ForClient(TestKeys.StaticKey());
        Assert.Null(client.Unwrap(client.Wrap(new byte[TlsCrypt.HeaderLength], [1, 2, 3])));
    }

    [Theory]
    [InlineData(0)]     // the opcode, in the clear but authenticated
    [InlineData(12)]    // the replay id
    [InlineData(20)]    // the tag
    [InlineData(60)]    // the ciphertext
    public void AnyChangedByteIsRejected(int at)
    {
        var key = TestKeys.StaticKey();
        var packet = TlsCrypt.ForClient(key).Wrap(new byte[TlsCrypt.HeaderLength], new byte[32]);
        packet[at] ^= 0x01;
        Assert.Null(new TlsCrypt(key, sendHalf: 0).Unwrap(packet));
    }

    [Fact]
    public void ADifferentKeyIsRejected()
    {
        var packet = TlsCrypt.ForClient(TestKeys.StaticKey()).Wrap(new byte[TlsCrypt.HeaderLength], new byte[8]);
        Assert.Null(new TlsCrypt(TestKeys.StaticKey(), sendHalf: 0).Unwrap(packet));
    }

    [Fact]
    public void TheClientSendsWithTheKeysSecondHalf()
    {
        // Pins the layout: the client's cipher key is bytes 128..159, its HMAC key bytes 192..223. Changing any other
        // byte of the key leaves what the client sends unchanged.
        var key = TestKeys.StaticKey();
        var header = new byte[TlsCrypt.HeaderLength];
        var before = TlsCrypt.ForClient(key).Wrap(header, new byte[16]);
        var other = (byte[])key.Clone();
        for (var i = 0; i < 256; i++) if (i is not (>= 128 and < 160 or >= 192 and < 224)) other[i] ^= 0xff;
        Assert.Equal(before, TlsCrypt.ForClient(other).Wrap(header, new byte[16]));
        other[130] ^= 0xff;
        Assert.NotEqual(before, TlsCrypt.ForClient(other).Wrap(header, new byte[16]));
    }
}
