#include "voice_codec.h"
#include "opus.h"
#include "rnnoise.h"
#include <math.h>
#include <string.h>
#include <stdlib.h>

namespace {

constexpr int kRate = 48000;
constexpr int kFrame = 960;
constexpr int kRnnoise = 480;
constexpr int kBitrate = 48000;
constexpr float kVadOpen = 0.85f;
constexpr int kVadGraceHops = 20; // 200 ms at 10 ms hops, same as werman.

int16_t Clamp16(float y)
{
    if (y > 32767.f) return 32767;
    if (y < -32768.f) return -32768;
    return (int16_t)y;
}

}

struct VoiceTx
{
    DenoiseState* ns = nullptr;
    OpusEncoder* enc = nullptr;
    int graceHops = 0;
    float gate = 0.f;
};

struct VoiceRx
{
    OpusDecoder* dec = nullptr;
};

VoiceTx* VoiceTxStart()
{
    VoiceTx* tx = new VoiceTx{};
    int err = OPUS_OK;
    tx->ns = rnnoise_create(nullptr);
    tx->enc = opus_encoder_create(kRate, 1, OPUS_APPLICATION_VOIP, &err);
    if (tx->enc == nullptr || err != OPUS_OK)
    {
        if (tx->ns != nullptr) rnnoise_destroy(tx->ns);
        delete tx;
        return nullptr;
    }
    opus_encoder_ctl(tx->enc, OPUS_SET_BITRATE(kBitrate));
    opus_encoder_ctl(tx->enc, OPUS_SET_COMPLEXITY(10));
    opus_encoder_ctl(tx->enc, OPUS_SET_SIGNAL(OPUS_SIGNAL_VOICE));
    opus_encoder_ctl(tx->enc, OPUS_SET_INBAND_FEC(1));
    opus_encoder_ctl(tx->enc, OPUS_SET_PACKET_LOSS_PERC(10));
    opus_encoder_ctl(tx->enc, OPUS_SET_LSB_DEPTH(16));
    opus_encoder_ctl(tx->enc, OPUS_SET_DTX(0));
    return tx;
}

void VoiceTxStop(VoiceTx* tx)
{
    if (tx == nullptr) return;
    if (tx->enc != nullptr) opus_encoder_destroy(tx->enc);
    if (tx->ns != nullptr) rnnoise_destroy(tx->ns);
    delete tx;
}

void VoiceTxBitrate(VoiceTx* tx, int bitrate)
{
    (void)tx;
    (void)bitrate;
    // HD2 asks Party for 12 kbps. That is telephone audio. The stand-in keeps 48 kbps.
}

int VoiceTxEncode(VoiceTx* tx, const int16_t* pcm, uint8_t* packet, int packetCap)
{
    if (tx == nullptr || tx->enc == nullptr || pcm == nullptr || packet == nullptr || packetCap < 8) return 0;
    float hop[kRnnoise];
    int16_t clean[kFrame];
    float vad = 0.f;
    for (int part = 0; part < 2; part++)
    {
        const int16_t* src = pcm + part * kRnnoise;
        for (int i = 0; i < kRnnoise; i++) hop[i] = (float)src[i];
        float partVad = 0.f;
        if (tx->ns != nullptr) partVad = rnnoise_process_frame(tx->ns, hop, hop);
        if (partVad < 0.f) partVad = 0.f;
        if (partVad > 1.f) partVad = 1.f;
        vad += partVad;
        if (partVad >= kVadOpen) tx->graceHops = kVadGraceHops;
        else if (tx->graceHops > 0) tx->graceHops--;
        float target = (partVad >= kVadOpen || tx->graceHops > 0) ? 1.f : 0.f;
        float coeff = target > tx->gate ? 0.45f : 0.18f;
        tx->gate += (target - tx->gate) * coeff;
        int16_t* dest = clean + part * kRnnoise;
        for (int i = 0; i < kRnnoise; i++) dest[i] = Clamp16(hop[i] * tx->gate);
    }
    vad *= 0.5f;
    int vadByte = 0;
    if (tx->gate > 0.05f)
    {
        vadByte = (int)(vad * 255.f + 0.5f);
        if (vadByte < 80) vadByte = 80;
        if (vadByte > 255) vadByte = 255;
    }
    packet[0] = (uint8_t)vadByte;
    int encoded = opus_encode(tx->enc, clean, kFrame, packet + 1, packetCap - 1);
    if (encoded < 1) return 0;
    return encoded + 1;
}

VoiceRx* VoiceRxStart()
{
    VoiceRx* rx = new VoiceRx{};
    int err = OPUS_OK;
    rx->dec = opus_decoder_create(kRate, 1, &err);
    if (rx->dec == nullptr || err != OPUS_OK)
    {
        delete rx;
        return nullptr;
    }
    return rx;
}

void VoiceRxStop(VoiceRx* rx)
{
    if (rx == nullptr) return;
    if (rx->dec != nullptr) opus_decoder_destroy(rx->dec);
    delete rx;
}

int VoiceRxDecode(VoiceRx* rx, const uint8_t* packet, int len, int16_t* pcm)
{
    if (rx == nullptr || rx->dec == nullptr || pcm == nullptr) return 0;
    const unsigned char* payload = nullptr;
    int payloadLen = 0;
    if (packet != nullptr && len >= 2)
    {
        payload = packet + 1;
        payloadLen = len - 1;
    }
    int got = opus_decode(rx->dec, payload, payloadLen, pcm, kFrame, 0);
    if (got != kFrame && payload != nullptr)
        got = opus_decode(rx->dec, nullptr, 0, pcm, kFrame, 0);
    if (got == kFrame) return 1;
    memset(pcm, 0, kFrame * sizeof(int16_t));
    return 1;
}
