# The VST3 effect the tests host: VoiceChangerTest.vst3, with moduleinfo.json.
smtg_add_vst3plugin(VoiceChangerTest testplugin.cpp)
target_link_libraries(VoiceChangerTest PRIVATE sdk)
