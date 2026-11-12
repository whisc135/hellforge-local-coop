// Opus VOIP plus RNNoise on capture (Xiph demo / OBS / werman).
// 20 ms of 48 kHz mono, 48 kbps, FEC. Every 10 ms hop is 100% RNNoise.
// Frames below VAD 0.85 are muted after a 200 ms hold, so noise in the gaps is gone.
// The other machine decodes Opus, with packet-loss concealment when a frame does not arrive.
#pragma once
#include <stdint.h>

struct VoiceTx;
struct VoiceRx;

VoiceTx* VoiceTxStart();
void VoiceTxStop(VoiceTx* tx);
void VoiceTxBitrate(VoiceTx* tx, int bitrate);
// pcm is 960 int16 samples. packet[0] is VAD 0-255, the rest is Opus. Returns total bytes, or 0.
int VoiceTxEncode(VoiceTx* tx, const int16_t* pcm, uint8_t* packet, int packetCap);

VoiceRx* VoiceRxStart();
void VoiceRxStop(VoiceRx* rx);
// packet null/len 0 is concealment. Always writes 960 samples. Returns 1.
int VoiceRxDecode(VoiceRx* rx, const uint8_t* packet, int len, int16_t* pcm);
