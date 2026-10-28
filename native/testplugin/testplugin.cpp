// "VoiceChanger Test Effect": a small VST3 effect the tests host through vcplugin. It has one
// parameter of each kind the host maps (continuous, list, on/off, stepped, bypass, hidden), a
// side-chain input, latency it reports, and state it saves.
//
//   output = delay(hold(input * gain + sidechain when "Sidechain Mix" is on), latency)
//
// "Crash" makes the next processed block write through a null pointer, for the host's crash guard.

#include "base/source/fstreamer.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "public.sdk/source/vst/vstaudioeffect.h"
#include "public.sdk/source/vst/vsteditcontroller.h"
// Last: it replaces the factory macros the other headers define.
#include "public.sdk/source/main/pluginfactory_constexpr.h"

#include <algorithm>
#include <cmath>
#include <vector>

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
    kParamCount = 6
};

constexpr double kMinGainDb = -24;
constexpr double kMaxGainDb = 12;
constexpr int32 kLatencies[] = {0, 64, 480};
constexpr int32 kLatencySteps = 2;
constexpr int32 kHoldSteps = 7; // hold 1 to 8 samples
constexpr int32 kStateVersion = 1;

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
                if (queue && queue->getParameterId() < kParamCount && queue->getPoint(queue->getPointCount() - 1, offset, value) == kResultTrue)
                    values[queue->getParameterId()] = value;
            }
        }

        if (data.numSamples <= 0 || data.numOutputs < 1)
            return kResultOk;

        if (values[kCrash] > 0.5)
        {
            volatile int* nowhere = nullptr;
            *nowhere = 1;
        }

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

class TestController : public EditController
{
public:
    static FUnknown* createInstance(void*) { return static_cast<IEditController*>(new TestController()); }

    tresult PLUGIN_API initialize(FUnknown* context) override
    {
        tresult result = EditController::initialize(context);
        if (result != kResultOk)
            return result;

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
        return kResultOk;
    }

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

    // A new latency takes effect when the host restarts the processor.
    tresult PLUGIN_API setParamNormalized(ParamID tag, ParamValue value) override
    {
        int32 before = stepOf(getParamNormalized(tag), kLatencySteps);
        tresult result = EditController::setParamNormalized(tag, value);
        if (tag == kLatency && result == kResultOk && componentHandler && stepOf(getParamNormalized(tag), kLatencySteps) != before)
            componentHandler->restartComponent(kLatencyChanged);
        return result;
    }

private:
    int32 saves = 0;
};

} // namespace

BEGIN_FACTORY_DEF("VoiceChanger", "https://github.com/CoffeeRoll/VoiceChanger", "", 2)

DEF_CLASS(ProcessorUID, PClassInfo::kManyInstances, kVstAudioEffectClass, "VoiceChanger Test Effect",
          Vst::kDistributable, "Fx|Tools", "1.0.0", kVstVersionString, TestProcessor::createInstance, nullptr)

DEF_CLASS(ControllerUID, PClassInfo::kManyInstances, kVstComponentControllerClass, "VoiceChanger Test Effect Controller",
          0, "", "1.0.0", kVstVersionString, TestController::createInstance, nullptr)

END_FACTORY
