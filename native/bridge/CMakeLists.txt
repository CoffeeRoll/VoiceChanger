# vcplugin: the C API VoiceChanger loads to host VST3 effects.
set(hosting "${SDK_ROOT}/public.sdk/source/vst/hosting")
set(vcplugin_sources
    vcplugin.cpp
    vcplugin.h
    "${hosting}/plugprovider.cpp"
    "${hosting}/plugprovider.h"
    "${SDK_ROOT}/public.sdk/source/common/memorystream.cpp"
)
if(SMTG_WIN)
    list(APPEND vcplugin_sources "${hosting}/module_win32.cpp")
elseif(SMTG_MAC)
    list(APPEND vcplugin_sources "${hosting}/module_mac.mm")
    set_source_files_properties("${hosting}/module_mac.mm" PROPERTIES COMPILE_FLAGS "-fobjc-arc")
else()
    list(APPEND vcplugin_sources "${hosting}/module_linux.cpp")
endif()

add_library(vcplugin SHARED ${vcplugin_sources})
target_link_libraries(vcplugin PRIVATE sdk_hosting)
if(SMTG_LINUX)
    target_link_libraries(vcplugin PRIVATE ${CMAKE_DL_LIBS} pthread)
elseif(SMTG_MAC)
    target_link_libraries(vcplugin PRIVATE "-framework CoreFoundation" "-framework Foundation")
endif()
if(MSVC)
    # Structured exceptions (a plug-in crashing) are caught with __try in vc_process.
    target_compile_options(vcplugin PRIVATE /EHa)
endif()
# A generator expression keeps multi-config generators from adding a Release/ folder.
set_target_properties(vcplugin PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "$<1:${VC_OUTPUT_DIR}>"
    LIBRARY_OUTPUT_DIRECTORY "$<1:${VC_OUTPUT_DIR}>"
    C_VISIBILITY_PRESET hidden
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
)
