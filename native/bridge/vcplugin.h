/*
 * vcplugin: a small C API for hosting VST3 effect plug-ins, used by VoiceChanger through P/Invoke.
 *
 * Threading: vc_process runs on the audio thread and never allocates (after its first blocks) or
 * locks. Everything else runs on one other thread (the UI thread), and vc_setup / vc_suspend /
 * vc_state_set / vc_close only while vc_process is not running for that plug-in.
 *
 * Strings are UTF-8. Functions that fill a text buffer truncate to its capacity and always
 * terminate it. Functions returning int return 0 (or a count) on success and a negative error
 * code on failure; vc_last_error gives the reason.
 */
#ifndef VCPLUGIN_H
#define VCPLUGIN_H

#ifdef _WIN32
#define VC_API __declspec(dllexport)
#else
#define VC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define VC_API_VERSION 1

enum vc_error
{
    VC_OK = 0,
    VC_ERROR_FAILED = -1,       /* the plug-in refused or something went wrong: see vc_last_error */
    VC_ERROR_ARGUMENT = -2,     /* a null handle, a bad index, or a buffer too small */
    VC_ERROR_NOT_ACTIVE = -3,   /* vc_process before vc_setup */
    VC_ERROR_CRASHED = -4       /* the plug-in crashed earlier and is now silent (Windows only) */
};

/* Classes of a module that are audio effects. */
typedef struct vc_class_info
{
    char class_id[40];      /* 32 hexadecimal digits */
    char name[128];
    char vendor[128];
    char version[64];
    char sdk_version[64];
    char subcategories[128]; /* e.g. "Fx|Delay" */
} vc_class_info;

typedef struct vc_plugin_info
{
    char name[128];
    char vendor[128];
    char input_name[64];     /* name of the main input bus */
    char sidechain_name[64]; /* name of the first auxiliary input bus, empty when there is none */
    int has_sidechain;
    int input_channels;      /* channels of the plug-in's main buses after vc_setup (1 or 2) */
    int output_channels;
} vc_plugin_info;

/* Flags of vc_param_info, as in Steinberg::Vst::ParameterInfo. */
enum vc_param_flags
{
    VC_PARAM_CAN_AUTOMATE = 1 << 0,
    VC_PARAM_READ_ONLY = 1 << 1,
    VC_PARAM_WRAP = 1 << 2,
    VC_PARAM_LIST = 1 << 3,
    VC_PARAM_HIDDEN = 1 << 4,
    VC_PARAM_PROGRAM_CHANGE = 1 << 15,
    VC_PARAM_BYPASS = 1 << 16
};

typedef struct vc_param_info
{
    unsigned int id;
    int step_count;          /* 0 = continuous, 1 = on/off, n = n + 1 values */
    double default_value;    /* normalised, 0 to 1 */
    int flags;               /* vc_param_flags */
    char title[128];
    char short_title[64];
    char units[32];
} vc_param_info;

/* A new value for a parameter, applied at the start of the next processed block. */
typedef struct vc_param_change
{
    unsigned int id;
    double value;            /* normalised, 0 to 1 */
} vc_param_change;

/* Called (on the thread the plug-in calls from, normally the UI thread) when the plug-in's own
 * window changes a parameter, or when the plug-in asks the host to re-read something
 * (Steinberg::Vst::RestartFlags: 1 = reload, 2 = I/O changed, 4 = parameter values changed,
 * 8 = latency changed, 16 = parameter titles changed). */
typedef void (*vc_param_edited_callback)(void* user, unsigned int id, double value);
typedef void (*vc_restart_callback)(void* user, int flags);

typedef struct vc_plugin vc_plugin;

VC_API int vc_api_version(void);

/* The reason for the last failure on the calling thread. */
VC_API const char* vc_last_error(void);

/* Lists the audio effect classes of the module at path (a .vst3 bundle or file). Writes up to
 * capacity of them and returns how many there are, or a negative error. */
VC_API int vc_scan(const char* path, vc_class_info* classes, int capacity);

/* Loads the module (shared between instances) and creates the class, or returns null. */
VC_API vc_plugin* vc_open(const char* path, const char* class_id);
VC_API void vc_close(vc_plugin* plugin);

/* Prepares for processing: channels of the host (1 or 2) for the main and side-chain inputs and
 * the output, the sample rate and the largest block. The plug-in's buses are set to match where
 * it allows; otherwise the bridge converts between mono and stereo. Ends any previous setup. */
VC_API int vc_setup(vc_plugin* plugin, double sample_rate, int max_frames, int channels);

/* Stops processing (the opposite of vc_setup). */
VC_API void vc_suspend(vc_plugin* plugin);

/* Asks the plug-in to clear its signal history (setProcessing off and on). Audio thread. */
VC_API int vc_reset(vc_plugin* plugin);

VC_API int vc_info(vc_plugin* plugin, vc_plugin_info* info);

/* Processes one block (audio thread): per-channel buffers of the host's channel count. sidechain
 * may be null when nothing is connected; the plug-in then hears the main input on its side-chain
 * (what a compressor keyed from its own input expects). */
VC_API int vc_process(vc_plugin* plugin, const float* const* inputs, const float* const* sidechain,
                      float* const* outputs, int frames, const vc_param_change* changes, int change_count);

VC_API int vc_latency(vc_plugin* plugin);
VC_API int vc_tail(vc_plugin* plugin);

VC_API int vc_param_count(vc_plugin* plugin);
VC_API int vc_param_describe(vc_plugin* plugin, int index, vc_param_info* info);
VC_API double vc_param_get(vc_plugin* plugin, unsigned int id);

/* Tells the plug-in's controller (and its window) about a new value. The processor gets it
 * through vc_process. */
VC_API int vc_param_set(vc_plugin* plugin, unsigned int id, double value);

/* The plug-in's own text for a normalised value, e.g. "-6.0 dB". */
VC_API int vc_param_format(vc_plugin* plugin, unsigned int id, double value, char* text, int capacity);

/* The complete state (processor and controller). Returns the size; writes it when capacity is
 * large enough (call with a null buffer to ask for the size). */
VC_API int vc_state_get(vc_plugin* plugin, unsigned char* buffer, int capacity);
VC_API int vc_state_set(vc_plugin* plugin, const unsigned char* data, int length);

VC_API void vc_set_callbacks(vc_plugin* plugin, void* user, vc_param_edited_callback edited, vc_restart_callback restart);

#ifdef __cplusplus
}
#endif

#endif
