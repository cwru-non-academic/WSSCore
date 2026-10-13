#include "BleContext.h"

namespace {
using Handle = std::shared_ptr<wss::BleContext>;
int32_t Submit(wss_ble_context context, wss::Command command, uint64_t id, uint64_t session = 0) noexcept {
    try {
        if (!context || !id) return WSS_INVALID_ARGUMENT;
        return (*static_cast<Handle*>(context))->Submit(command, id, session);
    } catch (...) { return wss::ExceptionStatus(); }
}
}
extern "C" {
int32_t WSS_CALL wss_ble_get_abi_info(wss_ble_abi_info* info) {
    if (!info || info->struct_size != sizeof(wss_ble_abi_info)) return WSS_INVALID_ARGUMENT;
    *info = { sizeof(wss_ble_abi_info), 1, 1, sizeof(void*), sizeof(wss_ble_event), sizeof(wss_ble_advertisement), 3, 0 };
    return WSS_OK;
}
int32_t WSS_CALL wss_ble_create(uint32_t major, wss_ble_callback callback, void* user, wss_ble_context* context) {
    if (!context) return WSS_INVALID_ARGUMENT;
    *context = nullptr;
    if (major != 1) return WSS_UNSUPPORTED_ABI;
    if (!callback || sizeof(void*) != 8) return WSS_INVALID_ARGUMENT;
    try {
        auto handle = std::make_unique<Handle>(std::make_shared<wss::BleContext>(callback, user));
        (*handle)->StartWorker();
        *context = handle.release();
        return WSS_OK;
    } catch (...) { return wss::ExceptionStatus(); }
}
int32_t WSS_CALL wss_ble_initialize(wss_ble_context context, uint64_t id) { return Submit(context, wss::Command::Initialize, id); }
int32_t WSS_CALL wss_ble_query_adapter(wss_ble_context context, uint64_t id) { return Submit(context, wss::Command::QueryAdapter, id); }
int32_t WSS_CALL wss_ble_scan_start(wss_ble_context context, uint64_t id, uint64_t session) {
    return session ? Submit(context, wss::Command::Start, id, session) : WSS_INVALID_ARGUMENT;
}
int32_t WSS_CALL wss_ble_scan_stop(wss_ble_context context, uint64_t id, uint64_t session) {
    return session ? Submit(context, wss::Command::Stop, id, session) : WSS_INVALID_ARGUMENT;
}
int32_t WSS_CALL wss_ble_cancel(wss_ble_context context, uint64_t id) {
    try {
        if (!context || !id) return WSS_INVALID_ARGUMENT;
        return (*static_cast<Handle*>(context))->Cancel(id);
    } catch (...) { return wss::ExceptionStatus(); }
}
int32_t WSS_CALL wss_ble_nus_connect(wss_ble_context context, uint64_t id, uint64_t connection,
    wss_ble_nus_connect_options const* options) {
    try {
        if (!context || !id || !connection || !options) return WSS_INVALID_ARGUMENT;
        return (*static_cast<Handle*>(context))->SubmitGatt(wss::Command::Connect, id, connection, options, nullptr, 0, options->timeout_ms);
    } catch (...) { return wss::ExceptionStatus(); }
}
int32_t WSS_CALL wss_ble_nus_write(wss_ble_context context, uint64_t id, uint64_t connection,
    uint8_t const* bytes, uint32_t length, uint32_t timeout) {
    try {
        if (!context || !id || !connection) return WSS_INVALID_ARGUMENT;
        return (*static_cast<Handle*>(context))->SubmitGatt(wss::Command::Write, id, connection, nullptr, bytes, length, timeout);
    } catch (...) { return wss::ExceptionStatus(); }
}
int32_t WSS_CALL wss_ble_nus_disconnect(wss_ble_context context, uint64_t id, uint64_t connection, uint32_t timeout) {
    try {
        if (!context || !id || !connection) return WSS_INVALID_ARGUMENT;
        return (*static_cast<Handle*>(context))->SubmitGatt(wss::Command::Disconnect, id, connection, nullptr, nullptr, 0, timeout);
    } catch (...) { return wss::ExceptionStatus(); }
}
int32_t WSS_CALL wss_ble_destroy(wss_ble_context context) {
    if (!context) return WSS_OK;
    try {
        auto handle = static_cast<Handle*>(context);
        (*handle)->Shutdown();
        delete handle;
        return WSS_OK;
    } catch (...) { return wss::ExceptionStatus(); }
}
}
