if(CONFIG_SHINYGO60)
  zephyr_library()
  zephyr_library_include_directories(include)
  zephyr_library_include_directories(${APPLICATION_SOURCE_DIR}/include)
  zephyr_library_sources_ifdef(CONFIG_SHINYGO60_BATTERY_TELEMETRY src/battery_heartbeat.c)

  if(CONFIG_SHINYGO60_CENTRAL)
    zephyr_library_sources(src/shinygo60.c)
    zephyr_library_sources_ifdef(CONFIG_SHINYGO60_PROTOCOL src/protocol_codec.c)
    zephyr_library_sources_ifdef(CONFIG_SHINYGO60_PROTOCOL src/protocol.c)
    zephyr_library_sources_ifdef(CONFIG_SHINYGO60_PROTOCOL src/layer_control.c)
    zephyr_library_sources_ifdef(CONFIG_SHINYGO60_PROTOCOL src/layer_telemetry.c)
    zephyr_library_sources_ifdef(CONFIG_SHINYGO60_PROTOCOL src/battery_telemetry.c)
    zephyr_library_sources_ifdef(CONFIG_SHINYGO60_PROTOCOL src/usb_transport.c)
    zephyr_library_sources_ifdef(CONFIG_SHINYGO60_PROTOCOL src/ble_transport.c)

    zephyr_ld_options(
      -Wl,--wrap=zmk_keymap_layer_activate
      -Wl,--wrap=zmk_keymap_layer_deactivate
      -Wl,--wrap=zmk_keymap_layer_toggle
      -Wl,--wrap=zmk_keymap_layer_to
    )
  endif()
endif()
