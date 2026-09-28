# Copyright 2026 Charles Lee
# SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

cmake_minimum_required(VERSION 3.20.0)
find_package(Zephyr REQUIRED HINTS $ENV{ZEPHYR_BASE})
project(ble_bench_peripheral)

target_sources(app PRIVATE src/main.c)
