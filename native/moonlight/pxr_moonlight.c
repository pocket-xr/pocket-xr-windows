// Pocket XR for Windows: a flat C interface to moonlight-common-c for the C# app (PcScreens/Moonlight.cs).
// One stream per process: moonlight-common-c keeps its connection in process-wide globals, so each PC screen runs in
// its own PocketXR.exe --pc-screen process, as each runs in its own process on the phone.
// Video arrives as whole frames (the decode unit's buffers joined), audio as Opus packets. GPLv3.

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "Limelight.h"

#define PXR_API __declspec(dllexport)

typedef struct {
    void (*stage)(int kind, int stage, int errorCode); // kind 0 starting, 1 complete, 2 failed
    void (*started)(void);
    void (*terminated)(int errorCode);
    void (*log)(const char* message);
    void (*status)(int connectionStatus);
    int (*videoSetup)(int videoFormat, int width, int height, int fps);
    // A whole frame; returns DR_OK or DR_NEED_IDR.
    int (*videoFrame)(const unsigned char* data, int length, int frameType, int frameNumber,
                      unsigned long long receiveUs, unsigned long long enqueueUs, int hostLatencyTenthsMs);
    int (*audioInit)(int sampleRate, int channels, int streams, int coupledStreams, int samplesPerFrame,
                     const unsigned char* mapping);
    void (*audioSample)(const unsigned char* data, int length);
} PXR_CALLBACKS;

static PXR_CALLBACKS cb;
static unsigned char* frameBuf;
static int frameCap;

static void onStageStarting(int stage) { if (cb.stage) cb.stage(0, stage, 0); }
static void onStageComplete(int stage) { if (cb.stage) cb.stage(1, stage, 0); }
static void onStageFailed(int stage, int err) { if (cb.stage) cb.stage(2, stage, err); }
static void onStarted(void) { if (cb.started) cb.started(); }
static void onTerminated(int err) { if (cb.terminated) cb.terminated(err); }
static void onStatus(int s) { if (cb.status) cb.status(s); }

static void onLog(const char* format, ...) {
    char line[1024];
    va_list ap;
    va_start(ap, format);
    vsnprintf(line, sizeof(line), format, ap);
    va_end(ap);
    size_t n = strlen(line);
    while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r')) line[--n] = 0;
    if (cb.log) cb.log(line);
}

static int onVideoSetup(int videoFormat, int width, int height, int redrawRate, void* context, int drFlags) {
    (void)context; (void)drFlags;
    return cb.videoSetup ? cb.videoSetup(videoFormat, width, height, redrawRate) : 0;
}

static int onDecodeUnit(PDECODE_UNIT du) {
    if (du->fullLength > frameCap) {
        int cap = du->fullLength + 65536;
        unsigned char* b = (unsigned char*)realloc(frameBuf, (size_t)cap);
        if (b == NULL) return DR_NEED_IDR;
        frameBuf = b;
        frameCap = cap;
    }
    int at = 0;
    for (PLENTRY e = du->bufferList; e != NULL; e = e->next) {
        memcpy(frameBuf + at, e->data, (size_t)e->length);
        at += e->length;
    }
    if (!cb.videoFrame) return DR_OK;
    return cb.videoFrame(frameBuf, at, du->frameType, du->frameNumber, du->receiveTimeUs, du->enqueueTimeUs,
                         du->frameHostProcessingLatency);
}

static int onAudioInit(int audioConfiguration, const POPUS_MULTISTREAM_CONFIGURATION c, void* context, int arFlags) {
    (void)audioConfiguration; (void)context; (void)arFlags;
    return cb.audioInit ? cb.audioInit(c->sampleRate, c->channelCount, c->streams, c->coupledStreams, c->samplesPerFrame,
                                       c->mapping) : 0;
}

static void onAudioSample(char* data, int length) {
    if (cb.audioSample) cb.audioSample((const unsigned char*)data, length);
}

// Starts the stream (blocking until it is up or has failed); 0 on success. riKeyId is sent big-endian as the IV's
// first four bytes, as moonlight-android does. encryption: ENCFLG_* (Pocket XR asks for all).
PXR_API int PxrStart(const char* address, const char* appVersion, const char* gfeVersion, const char* rtspSessionUrl,
                     int serverCodecModeSupport, int width, int height, int fps, int bitrateKbps, int packetSize,
                     int streamingRemotely, int audioConfiguration, int supportedVideoFormats, int clientRefreshRateX100,
                     const unsigned char* riKey16, int riKeyId, int encryptionFlags, int colorSpace, int colorRange,
                     int videoCapabilities, const PXR_CALLBACKS* callbacks) {
    cb = *callbacks;

    SERVER_INFORMATION server;
    LiInitializeServerInformation(&server);
    server.address = address;
    server.serverInfoAppVersion = appVersion;
    server.serverInfoGfeVersion = gfeVersion;
    server.rtspSessionUrl = rtspSessionUrl;
    server.serverCodecModeSupport = serverCodecModeSupport;

    STREAM_CONFIGURATION config;
    LiInitializeStreamConfiguration(&config);
    config.width = width;
    config.height = height;
    config.fps = fps;
    config.bitrate = bitrateKbps;
    config.packetSize = packetSize;
    config.streamingRemotely = streamingRemotely;
    config.audioConfiguration = audioConfiguration;
    config.supportedVideoFormats = supportedVideoFormats;
    config.clientRefreshRateX100 = clientRefreshRateX100;
    config.encryptionFlags = encryptionFlags;
    config.colorSpace = colorSpace;
    config.colorRange = colorRange;
    memcpy(config.remoteInputAesKey, riKey16, 16);
    memset(config.remoteInputAesIv, 0, sizeof(config.remoteInputAesIv));
    config.remoteInputAesIv[0] = (char)((riKeyId >> 24) & 0xff);
    config.remoteInputAesIv[1] = (char)((riKeyId >> 16) & 0xff);
    config.remoteInputAesIv[2] = (char)((riKeyId >> 8) & 0xff);
    config.remoteInputAesIv[3] = (char)(riKeyId & 0xff);

    CONNECTION_LISTENER_CALLBACKS listener;
    LiInitializeConnectionCallbacks(&listener);
    listener.stageStarting = onStageStarting;
    listener.stageComplete = onStageComplete;
    listener.stageFailed = onStageFailed;
    listener.connectionStarted = onStarted;
    listener.connectionTerminated = onTerminated;
    listener.logMessage = onLog;
    listener.connectionStatusUpdate = onStatus;

    DECODER_RENDERER_CALLBACKS video;
    LiInitializeVideoCallbacks(&video);
    video.setup = onVideoSetup;
    video.submitDecodeUnit = onDecodeUnit;
    video.capabilities = videoCapabilities;

    AUDIO_RENDERER_CALLBACKS audio;
    LiInitializeAudioCallbacks(&audio);
    audio.init = onAudioInit;
    audio.decodeAndPlaySample = onAudioSample;
    audio.capabilities = CAPABILITY_DIRECT_SUBMIT;

    return LiStartConnection(&server, &config, &listener, &video, &audio, NULL, 0, NULL, 0);
}

PXR_API void PxrStop(void) { LiStopConnection(); }
PXR_API void PxrInterrupt(void) { LiInterruptConnection(); }
PXR_API const char* PxrLaunchQuery(void) { return LiGetLaunchUrlQueryParameters(); }
PXR_API const char* PxrStageName(int stage) { return LiGetStageName(stage); }
PXR_API unsigned long long PxrNowUs(void) { return LiGetMicroseconds(); }
PXR_API void PxrRequestIdr(void) { LiRequestIdrFrame(); }
PXR_API int PxrPendingVideoFrames(void) { return LiGetPendingVideoFrames(); }

PXR_API int PxrRtt(unsigned int* rttMs, unsigned int* varianceMs) {
    uint32_t r = 0, v = 0;
    bool ok = LiGetEstimatedRttInfo(&r, &v);
    *rttMs = r;
    *varianceMs = v;
    return ok ? 1 : 0;
}

PXR_API int PxrMousePosition(short x, short y, short refW, short refH) { return LiSendMousePositionEvent(x, y, refW, refH); }
PXR_API int PxrMouseMove(short dx, short dy) { return LiSendMouseMoveEvent(dx, dy); }
PXR_API int PxrMouseButton(int press, int button) {
    return LiSendMouseButtonEvent(press ? BUTTON_ACTION_PRESS : BUTTON_ACTION_RELEASE, button);
}
PXR_API int PxrScroll(short amount) { return LiSendHighResScrollEvent(amount); }
PXR_API int PxrHScroll(short amount) { return LiSendHighResHScrollEvent(amount); }
PXR_API int PxrKey(short keyCode, int down, int modifiers) {
    return LiSendKeyboardEvent(keyCode, down ? KEY_ACTION_DOWN : KEY_ACTION_UP, (char)modifiers);
}
PXR_API int PxrText(const char* utf8, unsigned int length) { return LiSendUtf8TextEvent(utf8, length); }
