// vcplugin: hosts VST3 effect plug-ins for VoiceChanger (see vcplugin.h).

#include "vcplugin.h"

#if defined(_WIN32)
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <cerrno>
#include <ctime>
#include <fcntl.h>
#include <semaphore.h>
#include <unistd.h>
#endif

#include "pluginterfaces/base/funknownimpl.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/gui/iplugviewcontentscalesupport.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/ivstunits.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/utility/stringconvert.h"
#include "public.sdk/source/vst/vstpresetfile.h"

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#if defined(_MSC_VER)
#include <excpt.h>
#endif

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

thread_local std::string lastError;

int fail(int code, std::string message)
{
    lastError = std::move(message);
    return code;
}

void copyText(char* target, size_t capacity, const std::string& text)
{
    if (!target || capacity == 0)
        return;
    size_t length = std::min(text.size(), capacity - 1);
    std::memcpy(target, text.data(), length);
    target[length] = 0;
}

template <size_t N>
void copyText(char (&target)[N], const std::string& text)
{
    copyText(target, N, text);
}

std::string utf8(const String128 text)
{
    return StringConvert::convert(text);
}

// One host application object (the plug-ins' host context) for every plug-in.
FUnknown* hostContext()
{
    static HostApplication application;
    static bool registered = [] {
        PluginContextFactory::instance().setPluginContext(&application);
        return true;
    }();
    (void)registered;
    return &application;
}

// Modules stay loaded while any of their plug-ins is open; opening another instance reuses them.
std::mutex modulesLock;
std::map<std::string, std::weak_ptr<VST3::Hosting::Module>> modules;

// The module to load from a bundle. The SDK looks in the bundle's folder for this process's
// architecture; an ARM64X binary (arm64x-win) loads into ARM64 and x64 processes alike, so a bundle
// with only that one is loaded from it directly.
std::string modulePath(const std::string& path)
{
#if defined(_WIN32)
    namespace fs = std::filesystem;
    std::error_code ignored;
    fs::path bundle = fs::u8path(path);
    if (!fs::is_directory(bundle, ignored))
        return path;
#if defined(_M_ARM64)
    const char* native = "arm64-win";
#else
    const char* native = "x86_64-win";
#endif
    fs::path contents = bundle / "Contents";
    fs::path arm64x = contents / "arm64x-win" / bundle.filename();
    if (!fs::exists(contents / native, ignored) && fs::exists(arm64x, ignored))
        return arm64x.u8string();
#endif
    return path;
}

VST3::Hosting::Module::Ptr loadModule(const std::string& path, std::string& error)
{
    std::lock_guard<std::mutex> lock(modulesLock);
    if (auto existing = modules[path].lock())
        return existing;
    auto module = VST3::Hosting::Module::create(modulePath(path), error);
    if (module)
        modules[path] = module;
    return module;
}

bool isEffect(const VST3::Hosting::ClassInfo& info)
{
    return info.category() == kVstAudioEffectClass;
}

class ComponentHandler : public U::Implements<U::Directly<IComponentHandler>>
{
public:
    void* user = nullptr;
    vc_param_edited_callback edited = nullptr;
    vc_restart_callback restart = nullptr;

    tresult PLUGIN_API beginEdit(ParamID) override { return kResultOk; }

    tresult PLUGIN_API performEdit(ParamID id, ParamValue value) override
    {
        if (edited)
            edited(user, id, value);
        return kResultOk;
    }

    tresult PLUGIN_API endEdit(ParamID) override { return kResultOk; }

    tresult PLUGIN_API restartComponent(int32 flags) override
    {
        if (restart)
            restart(user, flags);
        return kResultOk;
    }
};

// The host side of a plug-in's window: passes its resize requests on.
class PlugFrame : public U::Implements<U::Directly<IPlugFrame>>
{
public:
    explicit PlugFrame(vc_plugin* owner) : plugin(owner) {}

    tresult PLUGIN_API resizeView(IPlugView* view, ViewRect* newSize) override;

    vc_plugin* plugin;
};

int channelsOf(SpeakerArrangement arrangement)
{
    return SpeakerArr::getChannelCount(arrangement);
}

SpeakerArrangement arrangementFor(int channels)
{
    return channels == 1 ? SpeakerArr::kMono : SpeakerArr::kStereo;
}

#if defined(_MSC_VER)
// A plug-in that crashes while processing is caught here (structured exceptions only exist on
// Windows); no C++ objects may live in a function using __try.
int32 guardedProcess(IAudioProcessor* processor, ProcessData* data, bool* crashed)
{
    __try
    {
        return processor->process(*data);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *crashed = true;
        return kInternalError;
    }
}
#else
int32 guardedProcess(IAudioProcessor* processor, ProcessData* data, bool*)
{
    return processor->process(*data);
}
#endif

} // namespace

struct vc_plugin
{
    VST3::Hosting::Module::Ptr module;
    IPtr<PlugProvider> provider;
    IPtr<IComponent> component;
    IPtr<IAudioProcessor> processor;
    IPtr<IEditController> controller;
    IPtr<ComponentHandler> handler;
    VST3::UID classId;
    std::string name;
    std::string vendor;

    // The plug-in's window, while it is open.
    IPtr<IPlugView> view;
    IPtr<PlugFrame> frame;
    ViewRect viewSize {};
    vc_resize_callback resize = nullptr;

    HostProcessData data;
    ParameterChanges inputChanges {64};
    ParameterChanges outputChanges {64};
    ProcessContext context {};

    bool active = false;
    bool crashed = false;
    int channels = 0;
    int maxFrames = 0;
    int mainInput = -1;
    int sidechainInput = -1;
    int mainOutput = -1;
    int inputChannels = 0;
    int sidechainChannels = 0;
    int outputChannels = 0;

    // Buffers for buses and channels the host doesn't provide, and for mono/stereo conversion.
    std::vector<std::vector<float>> spare;
    std::vector<float> mixedInput;
    std::vector<float> mixedSidechain;
    std::vector<float> stereoLeft;
    std::vector<float> stereoRight;

    float* spareBuffer()
    {
        spare.emplace_back(static_cast<size_t>(maxFrames), 0.0f);
        return spare.back().data();
    }
};

tresult PLUGIN_API PlugFrame::resizeView(IPlugView* view, ViewRect* newSize)
{
    if (!newSize || !plugin || view != plugin->view)
        return kInvalidArgument;
    if (!plugin->resize || !plugin->resize(plugin->handler->user, newSize->getWidth(), newSize->getHeight()))
        return kResultFalse;

    // The host's window has that size now. When it already had, the editor wasn't told: tell it.
    if (plugin->viewSize.getWidth() != newSize->getWidth() || plugin->viewSize.getHeight() != newSize->getHeight())
    {
        plugin->viewSize = ViewRect(0, 0, newSize->getWidth(), newSize->getHeight());
        view->onSize(&plugin->viewSize);
    }

    return kResultTrue;
}

namespace {

void closeEditor(vc_plugin* plugin)
{
    if (!plugin->view)
        return;
    plugin->view->removed();
    plugin->view->setFrame(nullptr);
    plugin->view = nullptr;
}

// The program list of the plug-in's root unit (or its first one).
bool findProgramList(vc_plugin* plugin, IPtr<IUnitInfo>& units, ProgramListInfo& list)
{
    if (!plugin->controller)
        return false;
    units = U::cast<IUnitInfo>(plugin->controller);
    if (!units || units->getProgramListCount() <= 0)
        return false;

    ProgramListID wanted = kNoProgramListId;
    for (int32 i = 0; i < units->getUnitCount(); i++)
    {
        UnitInfo unit {};
        if (units->getUnitInfo(i, unit) == kResultTrue && unit.id == kRootUnitId)
        {
            wanted = unit.programListId;
            break;
        }
    }

    for (int32 i = 0; i < units->getProgramListCount(); i++)
    {
        if (units->getProgramListInfo(i, list) == kResultTrue && (wanted == kNoProgramListId || list.id == wanted))
            return true;
    }

    return false;
}

bool readFile(const char* path, std::vector<char>& bytes)
{
    std::ifstream file(std::filesystem::u8path(path), std::ios::binary);
    if (!file)
        return false;
    bytes.assign(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
    return !file.bad();
}

void suspend(vc_plugin* plugin)
{
    if (!plugin->active)
        return;
    plugin->processor->setProcessing(false);
    plugin->component->setActive(false);
    plugin->data.unprepare();
    plugin->active = false;
}

// Points the plug-in's input bus at the host's signal, converting between mono and stereo.
void connectInput(vc_plugin* plugin, int bus, int busChannels, const float* const* source, int frames, std::vector<float>& mixed)
{
    auto& buffers = plugin->data.inputs[bus];
    int host = plugin->channels;
    if (busChannels == 1 && host == 2)
    {
        for (int n = 0; n < frames; n++)
            mixed[n] = 0.5f * (source[0][n] + source[1][n]);
        buffers.channelBuffers32[0] = mixed.data();
        return;
    }

    for (int c = 0; c < busChannels; c++)
        buffers.channelBuffers32[c] = const_cast<float*>(source[std::min(c, host - 1)]);
}

} // namespace

extern "C" {

VC_API int vc_api_version(void)
{
    return VC_API_VERSION;
}

VC_API const char* vc_last_error(void)
{
    return lastError.c_str();
}

VC_API int vc_scan(const char* path, vc_class_info* classes, int capacity)
{
    if (!path || (capacity > 0 && !classes))
        return fail(VC_ERROR_ARGUMENT, "No path or buffer.");

    try
    {
        std::string error;
        auto module = loadModule(path, error);
        if (!module)
            return fail(VC_ERROR_FAILED, error.empty() ? "The module could not be loaded." : error);

        int count = 0;
        for (const auto& info : module->getFactory().classInfos())
        {
            if (!isEffect(info))
                continue;
            if (count < capacity)
            {
                auto& out = classes[count];
                std::memset(&out, 0, sizeof(out));
                copyText(out.class_id, info.ID().toString());
                copyText(out.name, info.name());
                copyText(out.vendor, info.vendor().empty() ? module->getFactory().info().vendor() : info.vendor());
                copyText(out.version, info.version());
                copyText(out.sdk_version, info.sdkVersion());
                copyText(out.subcategories, info.subCategoriesString());
            }
            count++;
        }

        return count;
    }
    catch (const std::exception& e)
    {
        return fail(VC_ERROR_FAILED, e.what());
    }
    catch (...)
    {
        return fail(VC_ERROR_FAILED, "The module threw an exception.");
    }
}

VC_API vc_plugin* vc_open(const char* path, const char* class_id)
{
    if (!path || !class_id)
    {
        fail(VC_ERROR_ARGUMENT, "No path or class.");
        return nullptr;
    }

    try
    {
        hostContext();
        std::string error;
        auto module = loadModule(path, error);
        if (!module)
        {
            fail(VC_ERROR_FAILED, error.empty() ? "The module could not be loaded." : error);
            return nullptr;
        }

        auto uid = VST3::UID::fromString(std::string(class_id));
        if (!uid)
        {
            fail(VC_ERROR_ARGUMENT, "Not a class id: " + std::string(class_id));
            return nullptr;
        }

        const auto& factory = module->getFactory();
        for (const auto& info : factory.classInfos())
        {
            if (info.ID() != *uid || !isEffect(info))
                continue;

            auto plugin = std::make_unique<vc_plugin>();
            plugin->module = module;
            plugin->provider = owned(new PlugProvider(factory, info, true));
            if (!plugin->provider->initialize())
            {
                fail(VC_ERROR_FAILED, "The plug-in could not be created: " + info.name());
                return nullptr;
            }

            plugin->component = plugin->provider->getComponentPtr();
            plugin->controller = plugin->provider->getControllerPtr();
            plugin->processor = U::cast<IAudioProcessor>(plugin->component);
            if (!plugin->processor)
            {
                fail(VC_ERROR_FAILED, "Not an audio processor: " + info.name());
                return nullptr;
            }

            plugin->classId = info.ID();
            plugin->name = info.name();
            plugin->vendor = info.vendor().empty() ? factory.info().vendor() : info.vendor();
            plugin->handler = owned(new ComponentHandler());
            plugin->frame = owned(new PlugFrame(plugin.get()));
            if (plugin->controller)
                plugin->controller->setComponentHandler(plugin->handler);
            return plugin.release();
        }

        fail(VC_ERROR_ARGUMENT, "No effect with class id " + std::string(class_id));
        return nullptr;
    }
    catch (const std::exception& e)
    {
        fail(VC_ERROR_FAILED, e.what());
        return nullptr;
    }
    catch (...)
    {
        fail(VC_ERROR_FAILED, "The plug-in threw an exception.");
        return nullptr;
    }
}

VC_API void vc_close(vc_plugin* plugin)
{
    if (!plugin)
        return;
    try
    {
        closeEditor(plugin);
        suspend(plugin);
        if (plugin->controller)
            plugin->controller->setComponentHandler(nullptr);
        plugin->processor = nullptr;
        plugin->controller = nullptr;
        plugin->component = nullptr;
        plugin->provider = nullptr; // terminates and disconnects both parts
    }
    catch (...)
    {
    }

    delete plugin;
}

VC_API int vc_setup(vc_plugin* plugin, double sample_rate, int max_frames, int channels)
{
    if (!plugin || max_frames < 1 || channels < 1 || channels > 2 || sample_rate <= 0)
        return fail(VC_ERROR_ARGUMENT, "Bad setup.");

    try
    {
        suspend(plugin);
        auto& component = *plugin->component;
        auto& processor = *plugin->processor;
        plugin->channels = channels;
        plugin->maxFrames = max_frames;
        plugin->crashed = false;

        // The first main bus each way, and the first auxiliary input (a side-chain).
        int inputs = component.getBusCount(kAudio, kInput);
        int outputs = component.getBusCount(kAudio, kOutput);
        plugin->mainInput = plugin->sidechainInput = plugin->mainOutput = -1;
        for (int i = 0; i < inputs; i++)
        {
            BusInfo info {};
            component.getBusInfo(kAudio, kInput, i, info);
            if (info.busType == kMain && plugin->mainInput < 0)
                plugin->mainInput = i;
            else if (info.busType == kAux && plugin->sidechainInput < 0)
                plugin->sidechainInput = i;
        }

        for (int i = 0; i < outputs; i++)
        {
            BusInfo info {};
            component.getBusInfo(kAudio, kOutput, i, info);
            if (info.busType == kMain && plugin->mainOutput < 0)
                plugin->mainOutput = i;
        }

        if (plugin->mainOutput < 0)
            return fail(VC_ERROR_FAILED, "The plug-in has no audio output.");

        // Ask for the host's channel count on the buses we use; failing that, stereo; failing
        // that, whatever the plug-in has (the bridge converts).
        std::vector<SpeakerArrangement> in(static_cast<size_t>(inputs)), out(static_cast<size_t>(outputs));
        auto current = [&] {
            for (int i = 0; i < inputs; i++)
                processor.getBusArrangement(kInput, i, in[i]);
            for (int i = 0; i < outputs; i++)
                processor.getBusArrangement(kOutput, i, out[i]);
        };
        auto request = [&](int wanted) {
            current();
            if (plugin->mainInput >= 0)
                in[plugin->mainInput] = arrangementFor(wanted);
            if (plugin->sidechainInput >= 0)
                in[plugin->sidechainInput] = arrangementFor(wanted);
            out[plugin->mainOutput] = arrangementFor(wanted);
            return processor.setBusArrangements(in.data(), inputs, out.data(), outputs) == kResultTrue;
        };
        if (!request(channels) && !(channels == 1 && request(2)))
            current();

        current();
        plugin->inputChannels = plugin->mainInput >= 0 ? std::min(2, channelsOf(in[plugin->mainInput])) : 0;
        plugin->sidechainChannels = plugin->sidechainInput >= 0 ? std::min(2, channelsOf(in[plugin->sidechainInput])) : 0;
        plugin->outputChannels = std::min(2, channelsOf(out[plugin->mainOutput]));
        if (plugin->outputChannels < 1)
            return fail(VC_ERROR_FAILED, "The plug-in's output has no channels.");

        for (int i = 0; i < inputs; i++)
            component.activateBus(kAudio, kInput, i, i == plugin->mainInput || i == plugin->sidechainInput);
        for (int i = 0; i < outputs; i++)
            component.activateBus(kAudio, kOutput, i, i == plugin->mainOutput);

        ProcessSetup setup {kRealtime, kSample32, max_frames, sample_rate};
        if (processor.setupProcessing(setup) != kResultTrue)
            return fail(VC_ERROR_FAILED, "The plug-in does not support this sample rate or block size.");

        if (component.setActive(true) != kResultTrue)
            return fail(VC_ERROR_FAILED, "The plug-in could not be activated.");

        // Bus buffer containers without samples: every channel gets a buffer here (spare ones
        // for buses and channels we don't use), the ones we use are pointed at the host's
        // buffers for each block.
        auto& data = plugin->data;
        data.prepare(component, 0, kSample32);
        plugin->spare.clear();
        plugin->spare.reserve(64);
        for (int i = 0; i < data.numInputs; i++)
            for (int c = 0; c < data.inputs[i].numChannels; c++)
                data.inputs[i].channelBuffers32[c] = plugin->spareBuffer();
        for (int i = 0; i < data.numOutputs; i++)
            for (int c = 0; c < data.outputs[i].numChannels; c++)
                data.outputs[i].channelBuffers32[c] = plugin->spareBuffer();

        plugin->mixedInput.assign(static_cast<size_t>(max_frames), 0.0f);
        plugin->mixedSidechain.assign(static_cast<size_t>(max_frames), 0.0f);
        plugin->stereoLeft.assign(static_cast<size_t>(max_frames), 0.0f);
        plugin->stereoRight.assign(static_cast<size_t>(max_frames), 0.0f);

        data.inputParameterChanges = &plugin->inputChanges;
        data.outputParameterChanges = &plugin->outputChanges;
        auto& context = plugin->context;
        context = {};
        context.state = ProcessContext::kTempoValid | ProcessContext::kTimeSigValid;
        context.sampleRate = sample_rate;
        context.tempo = 120;
        context.timeSigNumerator = 4;
        context.timeSigDenominator = 4;
        data.processContext = &context;

        processor.setProcessing(true);
        plugin->active = true;
        return VC_OK;
    }
    catch (const std::exception& e)
    {
        return fail(VC_ERROR_FAILED, e.what());
    }
    catch (...)
    {
        return fail(VC_ERROR_FAILED, "The plug-in threw an exception.");
    }
}

VC_API void vc_suspend(vc_plugin* plugin)
{
    if (!plugin)
        return;
    try
    {
        suspend(plugin);
    }
    catch (...)
    {
    }
}

VC_API int vc_reset(vc_plugin* plugin)
{
    if (!plugin)
        return VC_ERROR_ARGUMENT;
    if (!plugin->active)
        return VC_ERROR_NOT_ACTIVE;
    if (plugin->crashed)
        return VC_ERROR_CRASHED;
    try
    {
        plugin->processor->setProcessing(false);
        plugin->processor->setProcessing(true);
        return VC_OK;
    }
    catch (...)
    {
        return VC_ERROR_FAILED;
    }
}

VC_API int vc_info(vc_plugin* plugin, vc_plugin_info* info)
{
    if (!plugin || !info)
        return fail(VC_ERROR_ARGUMENT, "No plug-in or buffer.");

    std::memset(info, 0, sizeof(*info));
    copyText(info->name, plugin->name);
    copyText(info->vendor, plugin->vendor);
    // The same buses vc_setup picks: the first main input and the first auxiliary one.
    int inputs = plugin->component->getBusCount(kAudio, kInput);
    bool foundMain = false;
    for (int i = 0; i < inputs; i++)
    {
        BusInfo bus {};
        if (plugin->component->getBusInfo(kAudio, kInput, i, bus) != kResultTrue)
            continue;
        if (bus.busType == kMain && !foundMain)
        {
            foundMain = true;
            copyText(info->input_name, utf8(bus.name));
        }
        else if (bus.busType == kAux && !info->has_sidechain)
        {
            info->has_sidechain = 1;
            copyText(info->sidechain_name, utf8(bus.name));
        }
    }

    info->input_channels = plugin->inputChannels;
    info->output_channels = plugin->outputChannels;
    return VC_OK;
}

VC_API int vc_process(vc_plugin* plugin, const float* const* inputs, const float* const* sidechain,
                      float* const* outputs, int frames, const vc_param_change* changes, int change_count)
{
    if (!plugin || !inputs || !outputs || frames < 0)
        return VC_ERROR_ARGUMENT;
    if (!plugin->active || frames > plugin->maxFrames)
        return VC_ERROR_NOT_ACTIVE;
    if (plugin->crashed)
        return VC_ERROR_CRASHED;

    auto& data = plugin->data;
    plugin->inputChanges.clearQueue();
    plugin->outputChanges.clearQueue();
    for (int i = 0; i < change_count; i++)
    {
        int32 index = 0;
        if (auto* queue = plugin->inputChanges.addParameterData(changes[i].id, index))
        {
            int32 point = 0;
            queue->addPoint(0, changes[i].value, point);
        }
    }

    if (plugin->mainInput >= 0)
        connectInput(plugin, plugin->mainInput, plugin->inputChannels, inputs, frames, plugin->mixedInput);
    if (plugin->sidechainInput >= 0)
        connectInput(plugin, plugin->sidechainInput, plugin->sidechainChannels, sidechain ? sidechain : inputs, frames, plugin->mixedSidechain);

    // The plug-in writes straight into the host's buffers when the channel counts match.
    auto& out = data.outputs[plugin->mainOutput];
    int host = plugin->channels;
    bool foldToMono = plugin->outputChannels == 2 && host == 1;
    if (foldToMono)
    {
        out.channelBuffers32[0] = plugin->stereoLeft.data();
        out.channelBuffers32[1] = plugin->stereoRight.data();
    }
    else
    {
        for (int c = 0; c < plugin->outputChannels; c++)
            out.channelBuffers32[c] = outputs[c];
    }

    for (int i = 0; i < data.numInputs; i++)
        data.inputs[i].silenceFlags = 0;
    for (int i = 0; i < data.numOutputs; i++)
        data.outputs[i].silenceFlags = 0;
    data.numSamples = frames;

    tresult result = kResultOk;
    try
    {
        result = guardedProcess(plugin->processor, &data, &plugin->crashed);
    }
    catch (...)
    {
        plugin->crashed = true;
    }

    if (plugin->crashed)
    {
        for (int c = 0; c < host; c++)
            std::memset(outputs[c], 0, sizeof(float) * static_cast<size_t>(frames));
        return VC_ERROR_CRASHED;
    }

    if (foldToMono)
    {
        for (int n = 0; n < frames; n++)
            outputs[0][n] = 0.5f * (plugin->stereoLeft[n] + plugin->stereoRight[n]);
    }
    else if (plugin->outputChannels == 1 && host == 2)
    {
        std::memcpy(outputs[1], outputs[0], sizeof(float) * static_cast<size_t>(frames));
    }

    plugin->context.projectTimeSamples += frames;
    plugin->context.continousTimeSamples += frames;
    return result == kResultOk || result == kResultTrue ? VC_OK : VC_ERROR_FAILED;
}

VC_API int vc_latency(vc_plugin* plugin)
{
    if (!plugin)
        return 0;
    try
    {
        return static_cast<int>(plugin->processor->getLatencySamples());
    }
    catch (...)
    {
        return 0;
    }
}

VC_API int vc_tail(vc_plugin* plugin)
{
    if (!plugin)
        return 0;
    try
    {
        return static_cast<int>(std::min<uint32>(plugin->processor->getTailSamples(), 0x7fffffff));
    }
    catch (...)
    {
        return 0;
    }
}

VC_API int vc_param_count(vc_plugin* plugin)
{
    if (!plugin || !plugin->controller)
        return 0;
    return plugin->controller->getParameterCount();
}

VC_API int vc_param_describe(vc_plugin* plugin, int index, vc_param_info* info)
{
    if (!plugin || !plugin->controller || !info)
        return fail(VC_ERROR_ARGUMENT, "No plug-in or buffer.");

    ParameterInfo parameter {};
    if (plugin->controller->getParameterInfo(index, parameter) != kResultTrue)
        return fail(VC_ERROR_ARGUMENT, "No parameter " + std::to_string(index));

    std::memset(info, 0, sizeof(*info));
    info->id = parameter.id;
    info->step_count = parameter.stepCount;
    info->default_value = parameter.defaultNormalizedValue;
    info->flags = parameter.flags;
    copyText(info->title, utf8(parameter.title));
    copyText(info->short_title, utf8(parameter.shortTitle));
    copyText(info->units, utf8(parameter.units));
    return VC_OK;
}

VC_API double vc_param_get(vc_plugin* plugin, unsigned int id)
{
    return plugin && plugin->controller ? plugin->controller->getParamNormalized(id) : 0;
}

VC_API int vc_param_set(vc_plugin* plugin, unsigned int id, double value)
{
    if (!plugin || !plugin->controller)
        return fail(VC_ERROR_ARGUMENT, "No plug-in.");
    return plugin->controller->setParamNormalized(id, value) == kResultTrue ? VC_OK : VC_ERROR_FAILED;
}

VC_API int vc_param_format(vc_plugin* plugin, unsigned int id, double value, char* text, int capacity)
{
    if (!plugin || !plugin->controller || !text || capacity < 1)
        return fail(VC_ERROR_ARGUMENT, "No plug-in or buffer.");

    String128 string {};
    if (plugin->controller->getParamStringByValue(id, value, string) != kResultTrue)
    {
        text[0] = 0;
        return VC_ERROR_FAILED;
    }

    copyText(text, static_cast<size_t>(capacity), utf8(string));
    return VC_OK;
}

// State layout: "VCS1", then the processor's state and the controller's, each as a 32-bit
// little-endian length followed by that many bytes.
VC_API int vc_state_get(vc_plugin* plugin, unsigned char* buffer, int capacity)
{
    if (!plugin)
        return fail(VC_ERROR_ARGUMENT, "No plug-in.");

    try
    {
        MemoryStream component, controller;
        plugin->component->getState(&component);
        if (plugin->controller)
            plugin->controller->getState(&controller);

        auto componentSize = static_cast<uint32_t>(component.getSize());
        auto controllerSize = static_cast<uint32_t>(controller.getSize());
        int total = 4 + 4 + static_cast<int>(componentSize) + 4 + static_cast<int>(controllerSize);
        if (!buffer || capacity < total)
            return total;

        unsigned char* p = buffer;
        auto put = [&p](uint32_t value) {
            for (int i = 0; i < 4; i++)
                *p++ = static_cast<unsigned char>(value >> (8 * i));
        };
        std::memcpy(p, "VCS1", 4);
        p += 4;
        put(componentSize);
        std::memcpy(p, component.getData(), componentSize);
        p += componentSize;
        put(controllerSize);
        std::memcpy(p, controller.getData(), controllerSize);
        return total;
    }
    catch (...)
    {
        return fail(VC_ERROR_FAILED, "The plug-in threw an exception while saving its state.");
    }
}

VC_API int vc_state_set(vc_plugin* plugin, const unsigned char* data, int length)
{
    if (!plugin || !data || length < 12 || std::memcmp(data, "VCS1", 4) != 0)
        return fail(VC_ERROR_ARGUMENT, "Not a VoiceChanger plug-in state.");

    auto get = [](const unsigned char* p) {
        uint32_t value = 0;
        for (int i = 0; i < 4; i++)
            value |= static_cast<uint32_t>(p[i]) << (8 * i);
        return value;
    };
    uint32_t componentSize = get(data + 4);
    if (12ull + componentSize > static_cast<uint64_t>(length))
        return fail(VC_ERROR_ARGUMENT, "The state is cut short.");
    uint32_t controllerSize = get(data + 8 + componentSize);
    if (12ull + componentSize + controllerSize > static_cast<uint64_t>(length))
        return fail(VC_ERROR_ARGUMENT, "The state is cut short.");

    try
    {
        auto* componentData = const_cast<unsigned char*>(data + 8);
        auto* controllerData = const_cast<unsigned char*>(data + 12 + componentSize);
        MemoryStream component(componentData, componentSize);
        if (plugin->component->setState(&component) != kResultTrue)
            return fail(VC_ERROR_FAILED, "The plug-in did not accept its saved state.");

        if (plugin->controller)
        {
            component.seek(0, IBStream::kIBSeekSet, nullptr);
            plugin->controller->setComponentState(&component);
            MemoryStream controller(controllerData, controllerSize);
            if (controllerSize > 0)
                plugin->controller->setState(&controller);
        }

        return VC_OK;
    }
    catch (...)
    {
        return fail(VC_ERROR_FAILED, "The plug-in threw an exception while loading its state.");
    }
}

VC_API void vc_set_callbacks(vc_plugin* plugin, void* user, vc_param_edited_callback edited, vc_restart_callback restart)
{
    if (!plugin)
        return;
    plugin->handler->user = user;
    plugin->handler->edited = edited;
    plugin->handler->restart = restart;
}

VC_API int vc_program_count(vc_plugin* plugin)
{
    if (!plugin)
        return 0;
    try
    {
        IPtr<IUnitInfo> units;
        ProgramListInfo list {};
        return findProgramList(plugin, units, list) ? std::max<int32>(0, list.programCount) : 0;
    }
    catch (...)
    {
        return 0;
    }
}

VC_API int vc_program_name(vc_plugin* plugin, int index, char* text, int capacity)
{
    if (!plugin || !text || capacity < 1)
        return fail(VC_ERROR_ARGUMENT, "No plug-in or buffer.");
    text[0] = 0;
    try
    {
        IPtr<IUnitInfo> units;
        ProgramListInfo list {};
        String128 name {};
        if (!findProgramList(plugin, units, list) || index < 0 || index >= list.programCount ||
            units->getProgramName(list.id, index, name) != kResultTrue)
            return fail(VC_ERROR_ARGUMENT, "No program " + std::to_string(index));
        copyText(text, static_cast<size_t>(capacity), utf8(name));
        return VC_OK;
    }
    catch (...)
    {
        return fail(VC_ERROR_FAILED, "The plug-in threw an exception.");
    }
}

VC_API long long vc_program_param(vc_plugin* plugin)
{
    if (!plugin || !plugin->controller)
        return -1;
    try
    {
        // The root unit's program parameter, or failing that any.
        long long found = -1;
        for (int32 i = 0; i < plugin->controller->getParameterCount(); i++)
        {
            ParameterInfo info {};
            if (plugin->controller->getParameterInfo(i, info) != kResultTrue || !(info.flags & ParameterInfo::kIsProgramChange))
                continue;
            if (info.unitId == kRootUnitId)
                return info.id;
            if (found < 0)
                found = info.id;
        }

        return found;
    }
    catch (...)
    {
        return -1;
    }
}

VC_API int vc_preset_load(vc_plugin* plugin, const char* path)
{
    if (!plugin || !path)
        return fail(VC_ERROR_ARGUMENT, "No plug-in or path.");
    try
    {
        std::vector<char> bytes;
        if (!readFile(path, bytes))
            return fail(VC_ERROR_FAILED, std::string("The preset could not be read: ") + path);
        MemoryStream stream(bytes.data(), static_cast<TSize>(bytes.size()));
        if (!PresetFile::loadPreset(&stream, FUID::fromTUID(plugin->classId.data()), plugin->component, plugin->controller))
            return fail(VC_ERROR_FAILED, "The preset is not for this plug-in, or the plug-in refused it.");
        return VC_OK;
    }
    catch (...)
    {
        return fail(VC_ERROR_FAILED, "The plug-in threw an exception while loading the preset.");
    }
}

VC_API int vc_preset_save(vc_plugin* plugin, const char* path)
{
    if (!plugin || !path)
        return fail(VC_ERROR_ARGUMENT, "No plug-in or path.");
    try
    {
        MemoryStream stream;
        if (!PresetFile::savePreset(&stream, FUID::fromTUID(plugin->classId.data()), plugin->component, plugin->controller))
            return fail(VC_ERROR_FAILED, "The plug-in could not save its state.");
        std::ofstream file(std::filesystem::u8path(path), std::ios::binary | std::ios::trunc);
        file.write(stream.getData(), static_cast<std::streamsize>(stream.getSize()));
        if (!file)
            return fail(VC_ERROR_FAILED, std::string("The preset could not be written: ") + path);
        return VC_OK;
    }
    catch (...)
    {
        return fail(VC_ERROR_FAILED, "The plug-in threw an exception while saving the preset.");
    }
}

VC_API int vc_editor_available(vc_plugin* plugin, const char* platform_type)
{
    if (!plugin || !plugin->controller || !platform_type)
        return 0;
    try
    {
        if (plugin->view)
            return plugin->view->isPlatformTypeSupported(platform_type) == kResultTrue;
        IPtr<IPlugView> view = owned(plugin->controller->createView(ViewType::kEditor));
        return view && view->isPlatformTypeSupported(platform_type) == kResultTrue;
    }
    catch (...)
    {
        return 0;
    }
}

VC_API int vc_editor_open(vc_plugin* plugin, void* parent, const char* platform_type, int* width, int* height, int* resizable)
{
    if (!plugin || !plugin->controller || !parent || !platform_type || !width || !height || !resizable)
        return fail(VC_ERROR_ARGUMENT, "No plug-in or window.");
    if (plugin->view)
        return fail(VC_ERROR_FAILED, "The plug-in's window is open already.");

    try
    {
        IPtr<IPlugView> view = owned(plugin->controller->createView(ViewType::kEditor));
        if (!view)
            return fail(VC_ERROR_FAILED, "The plug-in has no window.");
        if (view->isPlatformTypeSupported(platform_type) != kResultTrue)
            return fail(VC_ERROR_FAILED, std::string("The plug-in's window doesn't support ") + platform_type + ".");

        view->setFrame(plugin->frame);
        if (view->attached(parent, platform_type) != kResultOk)
        {
            view->setFrame(nullptr);
            return fail(VC_ERROR_FAILED, "The plug-in's window could not be shown.");
        }

        ViewRect size {};
        view->getSize(&size);
        plugin->view = view;
        plugin->viewSize = ViewRect(0, 0, size.getWidth(), size.getHeight());
        *width = size.getWidth();
        *height = size.getHeight();
        *resizable = view->canResize() == kResultTrue;
        return VC_OK;
    }
    catch (...)
    {
        return fail(VC_ERROR_FAILED, "The plug-in threw an exception while opening its window.");
    }
}

VC_API int vc_editor_set_size(vc_plugin* plugin, int width, int height)
{
    if (!plugin || !plugin->view || width < 0 || height < 0)
        return VC_ERROR_ARGUMENT;
    if (plugin->viewSize.getWidth() == width && plugin->viewSize.getHeight() == height)
        return VC_OK;
    try
    {
        plugin->viewSize = ViewRect(0, 0, width, height);
        return plugin->view->onSize(&plugin->viewSize) == kResultOk ? VC_OK : VC_ERROR_FAILED;
    }
    catch (...)
    {
        return VC_ERROR_FAILED;
    }
}

VC_API int vc_editor_constrain(vc_plugin* plugin, int* width, int* height)
{
    if (!plugin || !plugin->view || !width || !height)
        return VC_ERROR_ARGUMENT;
    try
    {
        ViewRect rect(0, 0, *width, *height);
        if (plugin->view->checkSizeConstraint(&rect) != kResultTrue)
            return VC_ERROR_FAILED;
        *width = rect.getWidth();
        *height = rect.getHeight();
        return VC_OK;
    }
    catch (...)
    {
        return VC_ERROR_FAILED;
    }
}

VC_API int vc_editor_set_scale(vc_plugin* plugin, float scale)
{
    if (!plugin || !plugin->view || scale <= 0)
        return VC_ERROR_ARGUMENT;
    try
    {
        auto support = U::cast<IPlugViewContentScaleSupport>(plugin->view);
        return support && support->setContentScaleFactor(scale) == kResultTrue ? VC_OK : VC_ERROR_FAILED;
    }
    catch (...)
    {
        return VC_ERROR_FAILED;
    }
}

VC_API void vc_editor_close(vc_plugin* plugin)
{
    if (!plugin)
        return;
    try
    {
        closeEditor(plugin);
    }
    catch (...)
    {
        plugin->view = nullptr;
    }
}

VC_API void vc_set_resize_callback(vc_plugin* plugin, vc_resize_callback resize)
{
    if (plugin)
        plugin->resize = resize;
}

// ---- Signals ----

struct vc_signal
{
#if defined(_WIN32)
    HANDLE handle = nullptr;
#else
    sem_t* semaphore = SEM_FAILED;
    std::string name;
    bool owner = false;
#endif
};

namespace {

bool validSignalName(const char* name)
{
    if (!name || !*name || std::strlen(name) > 30)
        return false;
    for (const char* c = name; *c; c++)
    {
        if (!((*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9') || *c == '-' || *c == '_'))
            return false;
    }
    return true;
}

vc_signal* openSignal(const char* name, bool create)
{
    if (!validSignalName(name))
    {
        fail(VC_ERROR_ARGUMENT, "Not a signal name.");
        return nullptr;
    }

    auto signal = std::make_unique<vc_signal>();
#if defined(_WIN32)
    std::string full = std::string("Local\\vc-") + name;
    if (create)
    {
        signal->handle = CreateSemaphoreA(nullptr, 0, 0x7fffffff, full.c_str());
        if (signal->handle && GetLastError() == ERROR_ALREADY_EXISTS)
        {
            CloseHandle(signal->handle);
            signal->handle = nullptr;
        }
    }
    else
    {
        signal->handle = OpenSemaphoreA(SEMAPHORE_MODIFY_STATE | SYNCHRONIZE, FALSE, full.c_str());
    }

    if (!signal->handle)
    {
        fail(VC_ERROR_FAILED, "The signal could not be " + std::string(create ? "created: " : "opened: ") + name);
        return nullptr;
    }
#else
    signal->name = std::string("/vc-") + name;
    signal->owner = create;
    signal->semaphore = create ? sem_open(signal->name.c_str(), O_CREAT | O_EXCL, 0600, 0) : sem_open(signal->name.c_str(), 0);
    if (signal->semaphore == SEM_FAILED)
    {
        fail(VC_ERROR_FAILED, "The signal could not be " + std::string(create ? "created: " : "opened: ") + name + " (" + std::strerror(errno) + ")");
        return nullptr;
    }
#endif
    return signal.release();
}

} // namespace

VC_API vc_signal* vc_signal_create(const char* name)
{
    return openSignal(name, true);
}

VC_API vc_signal* vc_signal_open(const char* name)
{
    return openSignal(name, false);
}

VC_API int vc_signal_post(vc_signal* signal)
{
    if (!signal)
        return VC_ERROR_ARGUMENT;
#if defined(_WIN32)
    return ReleaseSemaphore(signal->handle, 1, nullptr) ? VC_OK : VC_ERROR_FAILED;
#else
    return sem_post(signal->semaphore) == 0 ? VC_OK : VC_ERROR_FAILED;
#endif
}

VC_API int vc_signal_wait(vc_signal* signal, int timeout_ms)
{
    if (!signal)
        return VC_ERROR_ARGUMENT;
#if defined(_WIN32)
    DWORD result = WaitForSingleObject(signal->handle, timeout_ms < 0 ? INFINITE : static_cast<DWORD>(timeout_ms));
    return result == WAIT_OBJECT_0 ? 1 : result == WAIT_TIMEOUT ? 0 : VC_ERROR_FAILED;
#else
    if (timeout_ms < 0)
    {
        while (sem_wait(signal->semaphore) != 0)
        {
            if (errno != EINTR)
                return VC_ERROR_FAILED;
        }
        return 1;
    }

#if defined(__APPLE__)
    // No sem_timedwait on macOS: poll.
    for (int waited = 0;; waited++)
    {
        if (sem_trywait(signal->semaphore) == 0)
            return 1;
        if (errno != EAGAIN && errno != EINTR)
            return VC_ERROR_FAILED;
        if (waited >= timeout_ms * 10)
            return 0;
        usleep(100);
    }
#else
    timespec deadline {};
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += timeout_ms / 1000;
    deadline.tv_nsec += static_cast<long>(timeout_ms % 1000) * 1000000L;
    if (deadline.tv_nsec >= 1000000000L)
    {
        deadline.tv_sec++;
        deadline.tv_nsec -= 1000000000L;
    }

    while (sem_timedwait(signal->semaphore, &deadline) != 0)
    {
        if (errno == ETIMEDOUT)
            return 0;
        if (errno != EINTR)
            return VC_ERROR_FAILED;
    }
    return 1;
#endif
#endif
}

VC_API void vc_signal_close(vc_signal* signal)
{
    if (!signal)
        return;
#if defined(_WIN32)
    CloseHandle(signal->handle);
#else
    sem_close(signal->semaphore);
    if (signal->owner)
        sem_unlink(signal->name.c_str());
#endif
    delete signal;
}

} // extern "C"
