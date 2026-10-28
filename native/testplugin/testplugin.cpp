// "VoiceChanger Test Effect": a small VST3 effect the tests host through vcplugin. It has one
// parameter of each kind the host maps (continuous, list, on/off, stepped, bypass, hidden), a
// side-chain input, latency it reports, state it saves, a program list, and (on Windows) a window.
//
//   output = delay(hold(input * gain + sidechain when "Sidechain Mix" is on), latency)
//
// "Crash" makes the next processed block write through a null pointer, for the host's crash guard;
// "Hang" makes it never return, for an out-of-process host's watchdog. Setting "Editor Width"
// makes the window ask the host for that width; a click in the window turns the gain up (an edit
// the host hears of); "Editor Scale" shows the scale factor the host gave the window.

#if defined(_WIN32)
// Before anything includes windows.h: no min and max macros.
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#endif

#include "base/source/fstreamer.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/gui/iplugviewcontentscalesupport.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "public.sdk/source/common/pluginview.h"
#include "public.sdk/source/vst/vstaudioeffect.h"
#include "public.sdk/source/vst/vsteditcontroller.h"
// Last: it replaces the factory macros the other headers define.
#include "public.sdk/source/main/pluginfactory_constexpr.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <thread>
#include <vector>

#if SMTG_OS_WINDOWS
#include <windows.h>
#endif

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

DECLARE_UID(ProcessorUID, 0x325848CE, 0xAB9D4F16, 0xA6A4B952, 0x529295C5);
DECLARE_UID(ControllerUID, 0xCE144C69, 0x9FFA4B5E, 0xA4BDDCAD, 0x32F08035);

enum ParamIds : ParamID
{
    kGain = 0,
    kLatency = 1,
    kSidechainMix = 2,
    kBypass = 3,
    kHold = 4,
    kCrash = 5,
    kProgram = 6,     // the program list's parameter (its id is the list's)
    kHang = 7,
    kEditorWidth = 8,
    kEditorScale = 9,
    kParamCount = 10
};

constexpr double kMinGainDb = -24;
constexpr double kMaxGainDb = 12;
constexpr int32 kLatencies[] = {0, 64, 480};
constexpr int32 kLatencySteps = 2;
constexpr int32 kHoldSteps = 7; // hold 1 to 8 samples
constexpr int32 kStateVersion = 1;

// The factory programs: gain (dB) and hold (samples).
struct Program
{
    const char16_t* name;
    double gainDb;
    int32 hold;
};
constexpr Program kPrograms[] = {{u"Default", 0, 1}, {u"Loud", 6, 1}, {u"Crushed", 0, 4}};
constexpr int32 kProgramSteps = 2;

double gainValue(double decibels)
{
    return (decibels - kMinGainDb) / (kMaxGainDb - kMinGainDb);
}

double holdValue(int32 hold)
{
    return static_cast<double>(hold - 1) / kHoldSteps;
}

double defaultValue(ParamID id)
{
    switch (id)
    {
        case kGain: return -kMinGainDb / (kMaxGainDb - kMinGainDb);
        default: return 0;
    }
}

// A stepped parameter's step for a normalised value, as the SDK's parameters round it.
int32 stepOf(ParamValue value, int32 steps)
{
    return std::min(steps, static_cast<int32>(value * (steps + 1)));
}

class TestProcessor : public AudioEffect
{
public:
    TestProcessor()
    {
        setControllerClass(FUID::fromTUID(ControllerUID));
        for (ParamID id = 0; id < kParamCount; id++)
            values[id] = defaultValue(id);
    }

    static FUnknown* createInstance(void*) { return static_cast<IAudioProcessor*>(new TestProcessor()); }

    tresult PLUGIN_API initialize(FUnknown* context) override
    {
        tresult result = AudioEffect::initialize(context);
        if (result != kResultOk)
            return result;
        addAudioInput(STR16("Voice"), SpeakerArr::kStereo);
        addAudioInput(STR16("Key"), SpeakerArr::kMono, kAux, 0);
        addAudioOutput(STR16("Output"), SpeakerArr::kStereo);
        return kResultOk;
    }

    tresult PLUGIN_API setBusArrangements(SpeakerArrangement* inputs, int32 numIns, SpeakerArrangement* outputs, int32 numOuts) override
    {
        auto monoOrStereo = [](SpeakerArrangement arrangement) {
            return arrangement == SpeakerArr::kMono || arrangement == SpeakerArr::kStereo;
        };
        if (numIns != 2 || numOuts != 1 || inputs[0] != outputs[0] || !monoOrStereo(inputs[0]) || !monoOrStereo(inputs[1]))
            return kResultFalse;
        return AudioEffect::setBusArrangements(inputs, numIns, outputs, numOuts);
    }

    tresult PLUGIN_API setActive(TBool state) override
    {
        if (state)
        {
            SpeakerArrangement arrangement = 0;
            getBusArrangement(kOutput, 0, arrangement);
            int32 channels = SpeakerArr::getChannelCount(arrangement);
            latency = kLatencies[stepOf(values[kLatency], kLatencySteps)];
            delay.assign(static_cast<size_t>(channels), std::vector<float>(static_cast<size_t>(std::max(1, latency)), 0.0f));
            held.assign(static_cast<size_t>(channels), 0.0f);
            delayPosition = 0;
            holdCounter = 0;
        }

        return AudioEffect::setActive(state);
    }

    // Starting to process (again) clears the signal history.
    tresult PLUGIN_API setProcessing(TBool state) override
    {
        if (state)
        {
            for (auto& line : delay)
                std::fill(line.begin(), line.end(), 0.0f);
            std::fill(held.begin(), held.end(), 0.0f);
            delayPosition = 0;
            holdCounter = 0;
        }

        return AudioEffect::setProcessing(state);
    }

    uint32 PLUGIN_API getLatencySamples() override { return static_cast<uint32>(latency); }

    uint32 PLUGIN_API getTailSamples() override { return static_cast<uint32>(latency); }

    tresult PLUGIN_API process(ProcessData& data) override
    {
        if (auto* changes = data.inputParameterChanges)
        {
            for (int32 i = 0; i < changes->getParameterCount(); i++)
            {
                IParamValueQueue* queue = changes->getParameterData(i);
                ParamValue value = 0;
                int32 offset = 0;
                if (!queue || queue->getParameterId() >= kParamCount || queue->getPoint(queue->getPointCount() - 1, offset, value) != kResultTrue)
                    continue;
                values[queue->getParameterId()] = value;
                if (queue->getParameterId() == kProgram)
                {
                    const Program& program = kPrograms[stepOf(value, kProgramSteps)];
                    values[kGain] = gainValue(program.gainDb);
                    values[kHold] = holdValue(program.hold);
                }
            }
        }

        if (data.numSamples <= 0 || data.numOutputs < 1)
            return kResultOk;

        if (values[kCrash] > 0.5)
        {
            volatile int* nowhere = nullptr;
            *nowhere = 1;
        }

        while (values[kHang] > 0.5)
            std::this_thread::sleep_for(std::chrono::seconds(1));

        const AudioBusBuffers& input = data.inputs[0];
        const AudioBusBuffers* key = data.numInputs > 1 && getAudioInput(1)->isActive() ? &data.inputs[1] : nullptr;
        AudioBusBuffers& output = data.outputs[0];
        int32 channels = std::min(output.numChannels, static_cast<int32>(delay.size()));
        bool bypass = values[kBypass] > 0.5;
        bool mix = values[kSidechainMix] > 0.5 && key && key->numChannels > 0;
        float gain = static_cast<float>(std::pow(10.0, (kMinGainDb + values[kGain] * (kMaxGainDb - kMinGainDb)) / 20));
        int32 hold = bypass ? 1 : 1 + stepOf(values[kHold], kHoldSteps);

        for (int32 n = 0; n < data.numSamples; n++)
        {
            bool sample = holdCounter == 0;
            for (int32 c = 0; c < channels; c++)
            {
                float x = input.channelBuffers32[std::min(c, input.numChannels - 1)][n];
                if (!bypass)
                {
                    x *= gain;
                    if (mix)
                        x += key->channelBuffers32[std::min(c, key->numChannels - 1)][n];
                }

                if (sample)
                    held[c] = x;
                x = held[c];

                if (latency > 0)
                {
                    float delayed = delay[c][delayPosition];
                    delay[c][delayPosition] = x;
                    x = delayed;
                }

                output.channelBuffers32[c][n] = x;
            }

            holdCounter = (holdCounter + 1) % hold;
            if (latency > 0)
                delayPosition = (delayPosition + 1) % latency;
        }

        return kResultOk;
    }

    tresult PLUGIN_API setState(IBStream* state) override
    {
        IBStreamer streamer(state, kLittleEndian);
        int32 version = 0;
        if (!streamer.readInt32(version) || version != kStateVersion)
            return kResultFalse;
        for (ParamID id = 0; id < kCrash; id++)
        {
            double value = 0;
            if (!streamer.readDouble(value))
                return kResultFalse;
            values[id] = value;
        }

        return kResultOk;
    }

    tresult PLUGIN_API getState(IBStream* state) override
    {
        IBStreamer streamer(state, kLittleEndian);
        streamer.writeInt32(kStateVersion);
        for (ParamID id = 0; id < kCrash; id++)
            streamer.writeDouble(values[id]);
        return kResultOk;
    }

private:
    ParamValue values[kParamCount] {};
    int32 latency = 0;
    std::vector<std::vector<float>> delay;
    std::vector<float> held;
    int32 delayPosition = 0;
    int32 holdCounter = 0;
};

class TestController;

#if SMTG_OS_WINDOWS
// The window: a plain child window the size of the view. A click turns the gain all the way up.
class TestView : public CPluginView, public IPlugViewContentScaleSupport
{
public:
    TestView(TestController* owner, const ViewRect& size) : CPluginView(&size), controller(owner) {}

    tresult PLUGIN_API isPlatformTypeSupported(FIDString type) override
    {
        return std::strcmp(type, kPlatformTypeHWND) == 0 ? kResultTrue : kResultFalse;
    }

    tresult PLUGIN_API attached(void* parent, FIDString type) override;
    tresult PLUGIN_API removed() override;

    tresult PLUGIN_API onSize(ViewRect* newSize) override
    {
        CPluginView::onSize(newSize);
        if (window)
            MoveWindow(window, 0, 0, newSize->getWidth(), newSize->getHeight(), TRUE);
        return kResultOk;
    }

    tresult PLUGIN_API canResize() override { return kResultTrue; }

    tresult PLUGIN_API checkSizeConstraint(ViewRect* size) override
    {
        size->right = size->left + std::max<int32>(200, size->getWidth());
        size->bottom = size->top + std::max<int32>(100, size->getHeight());
        return kResultTrue;
    }

    tresult PLUGIN_API setContentScaleFactor(ScaleFactor factor) override;

    // Asks the host for a new width.
    void requestWidth(int32 width)
    {
        if (plugFrame)
        {
            ViewRect size(0, 0, width, getRect().getHeight());
            plugFrame->resizeView(this, &size);
        }
    }

    void click();

    DEFINE_INTERFACES
        DEF_INTERFACE(IPlugViewContentScaleSupport)
    END_DEFINE_INTERFACES(CPluginView)
    REFCOUNT_METHODS(CPluginView)

private:
    static LRESULT CALLBACK windowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_LBUTTONDOWN)
        {
            if (auto* view = reinterpret_cast<TestView*>(GetWindowLongPtrW(window, GWLP_USERDATA)))
                view->click();
            return 0;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    static HINSTANCE module()
    {
        HMODULE handle = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&windowProc), &handle);
        return handle;
    }

    TestController* controller;
    HWND window = nullptr;
};
#endif

class TestController : public EditControllerEx1
{
public:
    static FUnknown* createInstance(void*) { return static_cast<IEditController*>(new TestController()); }

    tresult PLUGIN_API initialize(FUnknown* context) override
    {
        tresult result = EditControllerEx1::initialize(context);
        if (result != kResultOk)
            return result;

        auto* programs = new ProgramList(STR16("Factory"), kProgram, kRootUnitId);
        for (const Program& program : kPrograms)
            programs->addProgram(reinterpret_cast<const TChar*>(program.name));
        addUnit(new Unit(STR16("Root"), kRootUnitId, kNoParentUnitId, kProgram));
        addProgramList(programs);

        parameters.addParameter(new RangeParameter(STR16("Gain"), kGain, STR16("dB"), kMinGainDb, kMaxGainDb, 0));

        auto* latencyParameter = new StringListParameter(STR16("Latency"), kLatency);
        latencyParameter->appendString(STR16("None"));
        latencyParameter->appendString(STR16("64 samples"));
        latencyParameter->appendString(STR16("480 samples"));
        parameters.addParameter(latencyParameter);

        parameters.addParameter(STR16("Sidechain Mix"), nullptr, 1, 0, ParameterInfo::kCanAutomate, kSidechainMix);
        parameters.addParameter(STR16("Bypass"), nullptr, 1, 0, ParameterInfo::kCanAutomate | ParameterInfo::kIsBypass, kBypass);
        parameters.addParameter(new RangeParameter(STR16("Hold"), kHold, STR16("samples"), 1, 1 + kHoldSteps, 1, kHoldSteps));
        parameters.addParameter(STR16("Crash"), nullptr, 1, 0, ParameterInfo::kIsHidden, kCrash);
        parameters.addParameter(programs->getParameter());
        parameters.addParameter(STR16("Hang"), nullptr, 1, 0, ParameterInfo::kIsHidden, kHang);
        parameters.addParameter(STR16("Editor Width"), nullptr, 0, 0, ParameterInfo::kIsHidden, kEditorWidth);
        parameters.addParameter(STR16("Editor Scale"), nullptr, 0, 0, ParameterInfo::kIsHidden | ParameterInfo::kIsReadOnly, kEditorScale);
        return kResultOk;
    }

    IPlugView* PLUGIN_API createView(FIDString name) override
    {
#if SMTG_OS_WINDOWS
        if (std::strcmp(name, ViewType::kEditor) == 0)
            return new TestView(this, ViewRect(0, 0, 400, 300));
#endif
        (void)name;
        return nullptr;
    }

#if SMTG_OS_WINDOWS
    // The view the controller talks to is the one on screen: a host may create views it never
    // attaches (to ask what they support) and release them again.
    void viewAttached(TestView* attached) { view = attached; }

    void viewRemoved(TestView* removed)
    {
        if (view == removed)
            view = nullptr;
    }

    // A click in the window: an edit, as a knob would make it.
    void edit(ParamID id, ParamValue value)
    {
        beginEdit(id);
        setParamNormalized(id, value);
        performEdit(id, value);
        endEdit(id);
    }
#endif

    tresult PLUGIN_API setComponentState(IBStream* state) override
    {
        IBStreamer streamer(state, kLittleEndian);
        int32 version = 0;
        if (!streamer.readInt32(version) || version != kStateVersion)
            return kResultFalse;
        for (ParamID id = 0; id < kCrash; id++)
        {
            double value = 0;
            if (!streamer.readDouble(value))
                return kResultFalse;
            setParamNormalized(id, value);
        }

        return kResultOk;
    }

    // The controller's own state: how often its state was saved, so the tests can tell it apart
    // from the processor's.
    tresult PLUGIN_API setState(IBStream* state) override
    {
        IBStreamer streamer(state, kLittleEndian);
        return streamer.readInt32(saves) ? kResultOk : kResultFalse;
    }

    tresult PLUGIN_API getState(IBStream* state) override
    {
        IBStreamer streamer(state, kLittleEndian);
        streamer.writeInt32(++saves);
        return kResultOk;
    }

    // A new latency takes effect when the host restarts the processor. A program sets the gain and
    // the hold, and the host is told to read them again.
    tresult PLUGIN_API setParamNormalized(ParamID tag, ParamValue value) override
    {
        int32 before = stepOf(getParamNormalized(tag), kLatencySteps);
        tresult result = EditControllerEx1::setParamNormalized(tag, value);
        if (result != kResultOk)
            return result;

        if (tag == kLatency && componentHandler && stepOf(getParamNormalized(tag), kLatencySteps) != before)
            componentHandler->restartComponent(kLatencyChanged);

        if (tag == kProgram)
        {
            const Program& program = kPrograms[stepOf(value, kProgramSteps)];
            EditControllerEx1::setParamNormalized(kGain, gainValue(program.gainDb));
            EditControllerEx1::setParamNormalized(kHold, holdValue(program.hold));
            if (componentHandler)
                componentHandler->restartComponent(kParamValuesChanged);
        }

#if SMTG_OS_WINDOWS
        if (tag == kEditorWidth && view)
            view->requestWidth(200 + static_cast<int32>(value * 400));
#endif
        return result;
    }

private:
    int32 saves = 0;
#if SMTG_OS_WINDOWS
    TestView* view = nullptr;
#endif
};

#if SMTG_OS_WINDOWS
tresult PLUGIN_API TestView::attached(void* parent, FIDString type)
{
    static ATOM windowClass = [] {
        WNDCLASSW description {};
        description.lpfnWndProc = &TestView::windowProc;
        description.hInstance = module();
        description.hCursor = LoadCursor(nullptr, IDC_ARROW);
        description.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        description.lpszClassName = L"VoiceChangerTestEditor";
        return RegisterClassW(&description);
    }();
    (void)windowClass;

    tresult result = CPluginView::attached(parent, type);
    if (result != kResultOk)
        return result;
    window = CreateWindowExW(0, L"VoiceChangerTestEditor", L"VoiceChanger Test Effect", WS_CHILD | WS_VISIBLE, 0, 0,
                             getRect().getWidth(), getRect().getHeight(), static_cast<HWND>(parent), nullptr, module(), nullptr);
    if (!window)
        return kResultFalse;
    SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
    controller->viewAttached(this);
    return kResultOk;
}

tresult PLUGIN_API TestView::removed()
{
    if (window)
        DestroyWindow(window);
    window = nullptr;
    controller->viewRemoved(this);
    return CPluginView::removed();
}

tresult PLUGIN_API TestView::setContentScaleFactor(ScaleFactor factor)
{
    controller->setParamNormalized(kEditorScale, factor / 4);
    return kResultTrue;
}

void TestView::click()
{
    controller->edit(kGain, 1.0);
}
#endif

} // namespace

BEGIN_FACTORY_DEF("VoiceChanger", "https://github.com/CoffeeRoll/VoiceChanger", "", 2)

DEF_CLASS(ProcessorUID, PClassInfo::kManyInstances, kVstAudioEffectClass, "VoiceChanger Test Effect",
          Vst::kDistributable, "Fx|Tools", "1.0.0", kVstVersionString, TestProcessor::createInstance, nullptr)

DEF_CLASS(ControllerUID, PClassInfo::kManyInstances, kVstComponentControllerClass, "VoiceChanger Test Effect Controller",
          0, "", "1.0.0", kVstVersionString, TestController::createInstance, nullptr)

END_FACTORY
