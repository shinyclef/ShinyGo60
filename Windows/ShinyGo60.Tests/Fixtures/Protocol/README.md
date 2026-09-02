# Protocol vectors

The protocol-v1.1 golden vectors live in `Custom Firmware/Module/tests/protocol/vectors` and are linked into the C# test output. The same 20-byte requests,
responses, and unsolicited events are validated by the native firmware codec and the Windows codec, independent of USB or Bluetooth transport framing.
