// vcplugin: hosts VST3 effect plug-ins for VoiceChanger (see vcplugin.h).

#include "vcplugin.h"

#include "pluginterfaces/base/funknownimpl.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/utility/stringconvert.h"

#include <algorithm>
#include <cstdint>
#include <cstring>
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

VST3::Hosting::Module::Ptr loadModule(const std::string& path, std::string& error)
{
    std::lock_guard<std::mutex> lock(modulesLock);
    if (auto existing = modules[path].lock())
        return existing;
    auto module = VST3::Hosting::Module::create(path, error);
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
    std::string name;
    std::string vendor;

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

namespace {

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

            plugin->name = info.name();
            plugin->vendor = info.vendor().empty() ? factory.info().vendor() : info.vendor();
            plugin->handler = owned(new ComponentHandler());
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

} // extern "C"
