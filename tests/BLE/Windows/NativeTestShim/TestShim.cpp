// Synthetic ABI implementation. Never copied into the production consumer output.
#include "wss_ble_windows.h"
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <functional>
#include <mutex>
#include <thread>
#include <vector>
#include <string>

namespace {
std::atomic<int32_t> mode{ 0 };
std::atomic<uint64_t> created{ 0 }, destroyed{ 0 }, callbacks{ 0 }, live{ 0 };
class Shim {
public:
    wss_ble_callback callback;
    void* user;
    int32_t scenario;
    std::mutex gate;
    std::condition_variable wake;
    std::deque<std::function<void()>> work;
    bool closing = false, initialized = false, cancelledStopOnce = false;
    uint64_t session = 0, heldQuery = 0;
    uint64_t connection = 0, lastConnection = 0, heldGatt = 0;
    uint32_t heldStage = 0, budget = 20, resources = 0;
    bool retiredWrite = false;
    wss_ble_nus_connect_options lastOptions{};
    std::string deviceId;
    std::vector<std::vector<uint8_t>> writes;
    std::thread worker;
    Shim(wss_ble_callback cb, void* state) : callback(cb), user(state), scenario(mode.load()) {
        worker = std::thread([this] {
            for (;;) {
                std::function<void()> action;
                {
                    std::unique_lock<std::mutex> lock(gate);
                    wake.wait(lock, [this] { return closing || !work.empty(); });
                    if (closing) return;
                    action = std::move(work.front()); work.pop_front();
                }
                try { action(); } catch (...) {}
            }
        });
        ++created; ++live;
    }
    ~Shim() {
        { std::lock_guard<std::mutex> lock(gate); closing = true; wake.notify_all(); }
        worker.join(); ++destroyed; --live;
    }
    int32_t Queue(std::function<void()> action) {
        std::lock_guard<std::mutex> lock(gate);
        if (closing) return WSS_CLOSING;
        work.push_back(std::move(action)); wake.notify_one(); return WSS_OK;
    }
    void Emit(wss_ble_event const& event) {
        { std::lock_guard<std::mutex> lock(gate); if (closing) return; }
        ++callbacks; callback(user, &event);
    }
    void Complete(uint64_t id, int32_t status = WSS_OK, uint32_t adapter = 0, int32_t hr = 0) {
        wss_ble_event event{ sizeof(wss_ble_event), WSS_COMPLETION, id, session, status, hr, 0, adapter, nullptr, 0 };
        Emit(event);
    }
    void Gatt(uint32_t kind, uint64_t id, uint64_t conn, uint32_t stage, int32_t status = WSS_OK,
        uint32_t gatt = 0, uint32_t protocol = UINT32_MAX, std::vector<uint8_t> const* bytes = nullptr) {
        wss_ble_gatt_event event{};
        event.base = { sizeof(event), kind, id, conn, status, status == WSS_NATIVE_FAILURE ? static_cast<int32_t>(0x80004005) : 0, 0, 0, nullptr, 0 };
        event.stage = stage; event.gatt_status = gatt; event.protocol_error = protocol; event.max_write_length = connection ? budget : 0;
        if (bytes) { event.data = bytes->data(); event.data_length = static_cast<uint32_t>(bytes->size()); }
        Emit(event.base);
    }
    void Drop() { connection = 0; resources = 0; }
    void Advertisement(uint32_t variant) {
        if (!session) return;
        std::vector<uint8_t> name{ 'W','S','S','-',0xc3,0xa9,'-',0xe5,0x88,0xba,0xe6,0xbf,0x80 };
        if (variant == 1) name.clear();
        std::vector<uint8_t> uuid{ 0x6e,0x40,0x00,static_cast<uint8_t>(variant == 1 ? 2 : 1),0xb5,0xa3,0xf3,0x93,0xe0,0xa9,0xe5,0x0e,0x24,0xdc,0xca,0x9e };
        wss_ble_advertisement payload{ sizeof(wss_ble_advertisement), variant == 1 ? uint32_t(WSS_ADDRESS_UNSPECIFIED) : uint32_t(WSS_ADDRESS_RANDOM),
            0xAABBCCDDEEFFULL, variant == 1 ? -127 : -42, variant == 1 ? 0u : 1u,
            name.data(), static_cast<uint32_t>(name.size()), 1, uuid.data() };
        wss_ble_event event{ sizeof(wss_ble_event), WSS_ADVERTISEMENT, 0, session, WSS_OK, 0, 0, 0, &payload, 0 };
        Emit(event);
        std::fill(name.begin(), name.end(), uint8_t(0xdd)); std::fill(uuid.begin(), uuid.end(), uint8_t(0xdd));
        payload.address = 0; // poison borrowed buffers immediately after callback
    }
};
template<typename F> int32_t Guard(F action) noexcept { try { return action(); } catch (...) { return WSS_NATIVE_FAILURE; } }
Shim* Get(wss_ble_context context) { return static_cast<Shim*>(context); }
}
extern "C" {
int32_t WSS_CALL wss_ble_get_abi_info(wss_ble_abi_info* info) {
    if (!info || info->struct_size != sizeof(wss_ble_abi_info)) return WSS_INVALID_ARGUMENT;
    *info = { sizeof(wss_ble_abi_info), mode == 1 ? 2u : 1u, 1u, sizeof(void*),
        mode == 2 ? 64u : static_cast<uint32_t>(sizeof(wss_ble_event)), sizeof(wss_ble_advertisement), 3u, 0 };
    return WSS_OK;
}
int32_t WSS_CALL wss_ble_create(uint32_t major, wss_ble_callback cb, void* user, wss_ble_context* context) {
    if (!context) return WSS_INVALID_ARGUMENT;
    *context = nullptr;
    if (major != 1) return WSS_UNSUPPORTED_ABI;
    if (!cb) return WSS_INVALID_ARGUMENT;
    return Guard([&] { *context = new Shim(cb, user); return WSS_OK; });
}
int32_t WSS_CALL wss_ble_initialize(wss_ble_context context, uint64_t id) {
    if (!context || !id) return WSS_INVALID_ARGUMENT;
    return Guard([&] { auto s = Get(context); return s->Queue([s, id] {
        if (s->scenario == 3) { s->Complete(id, WSS_ACCESS_DENIED, 0, static_cast<int32_t>(0x80070005)); return; }
        s->initialized = true; s->Complete(id);
        if (s->scenario == 6) s->Complete(id); // deliberately malformed native duplicate
    }); });
}
int32_t WSS_CALL wss_ble_query_adapter(wss_ble_context context, uint64_t id) {
    if (!context || !id) return WSS_INVALID_ARGUMENT;
    return Guard([&] { auto s = Get(context); return s->Queue([s, id] {
        if (!s->initialized) { s->Complete(id, WSS_INVALID_STATE); return; }
        if (s->scenario == 4) { s->heldQuery = id; return; }
        s->Complete(id, WSS_OK, WSS_ADAPTER_READY);
    }); });
}
int32_t WSS_CALL wss_ble_scan_start(wss_ble_context context, uint64_t id, uint64_t session) {
    if (!context || !id || !session) return WSS_INVALID_ARGUMENT;
    return Guard([&] { auto s = Get(context); return s->Queue([s, id, session] {
        if (!s->initialized || s->session) { s->Complete(id, WSS_INVALID_STATE); return; }
         s->session = session; s->Complete(id);
         if (s->scenario == 40 || s->scenario == 41) s->Advertisement(0);
    }); });
}
int32_t WSS_CALL wss_ble_scan_stop(wss_ble_context context, uint64_t id, uint64_t session) {
    if (!context || !id || !session) return WSS_INVALID_ARGUMENT;
    return Guard([&] { auto s = Get(context); return s->Queue([s, id] {
        if (s->scenario == 7 && !s->cancelledStopOnce) {
            s->cancelledStopOnce = true; s->Complete(id, WSS_CANCELLED); return;
        }
        s->session = 0; s->Complete(id);
    }); });
}
int32_t WSS_CALL wss_ble_cancel(wss_ble_context context, uint64_t id) {
    if (!context || !id) return WSS_INVALID_ARGUMENT;
    return Guard([&] { auto s = Get(context); return s->Queue([s, id] {
        if (s->heldQuery == id) { s->heldQuery = 0; s->Complete(id, WSS_CANCELLED); }
        if (s->heldGatt == id) {
            auto conn = s->connection; auto stage = s->heldStage;
            s->heldGatt = 0;
            if (stage == WSS_WRITE && s->scenario == 30) s->retiredWrite = true;
            s->Drop(); s->Gatt(WSS_GATT_COMPLETION, id, conn, stage, WSS_CANCELLED);
            if (stage == WSS_WRITE) s->Gatt(WSS_CONNECTION_TERMINATED, 0, conn, stage, WSS_CANCELLED);
        }
    }); });
}
int32_t WSS_CALL wss_ble_destroy(wss_ble_context context) {
    return Guard([&] { delete Get(context); return WSS_OK; });
}
int32_t WSS_CALL wss_ble_nus_connect(wss_ble_context context, uint64_t id, uint64_t conn, wss_ble_nus_connect_options const* options) {
    if (!context || !id || !conn || !options || options->struct_size != 88) return WSS_INVALID_ARGUMENT;
    return Guard([&] {
        auto s = Get(context); auto copy = *options;
        auto text = options->device_id_length ? std::string(reinterpret_cast<char const*>(options->device_id_utf8), options->device_id_length) : std::string();
        copy.device_id_utf8 = nullptr;
        return s->Queue([s, id, conn, copy, text] {
            if (!s->initialized || s->connection || conn <= s->lastConnection) { s->Gatt(WSS_GATT_COMPLETION, id, conn, 1, WSS_INVALID_STATE); return; }
            if (s->retiredWrite) { s->Gatt(WSS_GATT_COMPLETION, id, conn, 1, WSS_BUSY); return; }
            s->lastConnection = s->connection = conn; s->resources = 4; s->lastOptions = copy; s->deviceId = text;
            if ((s->scenario >= 20 && s->scenario <= 25) || s->scenario == 41) {
                uint32_t stage = s->scenario == 20 ? 2u : s->scenario == 21 || s->scenario == 23 ? 3u : s->scenario == 25 ? 5u : 4u;
                s->Drop(); s->Gatt(WSS_GATT_COMPLETION, id, conn, stage, WSS_NATIVE_FAILURE, s->scenario == 25 ? 2u : 0u, s->scenario == 25 ? 3u : UINT32_MAX); return;
            }
            if (s->scenario == 27) { s->heldGatt = id; s->heldStage = 5; return; }
             s->Gatt(WSS_GATT_COMPLETION, id, conn, 5);
             if (s->scenario == 40) { std::vector<uint8_t> value{ 0, 255 }; s->Gatt(WSS_NOTIFICATION, 0, conn, 8, WSS_OK, 0, UINT32_MAX, &value); }
        });
    });
}
int32_t WSS_CALL wss_ble_nus_write(wss_ble_context context, uint64_t id, uint64_t conn, uint8_t const* bytes, uint32_t length, uint32_t timeout) {
    if (!context || !id || !conn || !timeout || (length && !bytes)) return WSS_INVALID_ARGUMENT;
    return Guard([&] {
        auto s = Get(context); std::vector<uint8_t> copy;
        if (length) copy.assign(bytes, bytes + length);
        return s->Queue([s, id, conn, copy] {
            if (s->connection != conn) { s->Gatt(WSS_GATT_COMPLETION, id, conn, 6, WSS_INVALID_STATE); return; }
            if (s->heldGatt) { s->Gatt(WSS_GATT_COMPLETION, id, conn, 6, WSS_BUSY); return; }
            if (copy.size() > s->budget) { s->Gatt(WSS_GATT_COMPLETION, id, conn, 6, WSS_INVALID_ARGUMENT); return; }
            s->writes.push_back(copy);
            if (s->scenario == 26) { s->Gatt(WSS_GATT_COMPLETION, id, conn, 6, WSS_NATIVE_FAILURE, 2, 0x0d); return; }
            if (s->scenario == 28 || s->scenario == 30) { s->heldGatt = id; s->heldStage = 6; return; }
            s->Gatt(WSS_GATT_COMPLETION, id, conn, 6);
        });
    });
}
int32_t WSS_CALL wss_ble_nus_disconnect(wss_ble_context context, uint64_t id, uint64_t conn, uint32_t timeout) {
    if (!context || !id || !conn || !timeout) return WSS_INVALID_ARGUMENT;
    return Guard([&] { auto s = Get(context); return s->Queue([s, id, conn] {
        if (s->connection && s->connection != conn) { s->Gatt(WSS_GATT_COMPLETION, id, conn, 7, WSS_INVALID_STATE); return; }
        if (s->heldGatt) {
            auto held = s->heldGatt; auto stage = s->heldStage; s->heldGatt = 0;
            if (stage == 6 && s->scenario == 30) s->retiredWrite = true;
            s->Gatt(WSS_GATT_COMPLETION, held, conn, stage, WSS_CANCELLED);
        }
        if (s->scenario == 29 && s->connection) { s->heldGatt = id; s->heldStage = 7; return; }
        s->Drop(); s->Gatt(WSS_GATT_COMPLETION, id, conn, 7, WSS_OK, s->scenario == 32 ? 2u : 0u, s->scenario == 32 ? 3u : UINT32_MAX);
    }); });
}
// Test-only controls: never exported by production.
__declspec(dllexport) void WSS_CALL wss_test_mode(int32_t value) { mode = value; }
__declspec(dllexport) int32_t WSS_CALL wss_test_emit(wss_ble_context context, uint32_t variant) {
    if (!context) return WSS_INVALID_ARGUMENT;
    return Guard([&] { auto s = Get(context); return s->Queue([s, variant] { s->Advertisement(variant); }); });
}
__declspec(dllexport) void WSS_CALL wss_test_counters(uint64_t* made, uint64_t* freed, uint64_t* delivered, uint64_t* alive) {
    *made = created; *freed = destroyed; *delivered = callbacks; *alive = live;
}
__declspec(dllexport) int32_t WSS_CALL wss_test_barrier(wss_ble_context context, uint64_t id) {
    if (!context || !id) return WSS_INVALID_ARGUMENT;
    return Guard([&] { auto s = Get(context); return s->Queue([s, id] { s->Complete(id); }); });
}
__declspec(dllexport) int32_t WSS_CALL wss_test_nus_emit(wss_ble_context context, uint64_t conn, uint8_t const* bytes, uint32_t length, uint32_t kind, int32_t status) {
    if (!context || (length && !bytes)) return WSS_INVALID_ARGUMENT;
    return Guard([&] { auto s = Get(context); std::vector<uint8_t> copy; if (length) copy.assign(bytes, bytes + length);
        return s->Queue([s, conn, copy, kind, status]() mutable {
            if (kind == WSS_CONNECTION_TERMINATED) {
                if (s->heldGatt) { s->Gatt(WSS_GATT_COMPLETION, s->heldGatt, conn, s->heldStage, WSS_CANCELLED); s->heldGatt = 0; }
                s->Drop();
            }
            s->Gatt(kind, 0, conn, 8, status, UINT32_MAX, UINT32_MAX, kind == WSS_NOTIFICATION ? &copy : nullptr);
            std::fill(copy.begin(), copy.end(), uint8_t(0xdd));
        }); });
}
__declspec(dllexport) int32_t WSS_CALL wss_test_nus_release(wss_ble_context context) {
    return Guard([&] { auto s = Get(context); return s->Queue([s] {
        s->retiredWrite = false;
        if (s->heldGatt) { auto id = s->heldGatt; s->heldGatt = 0; s->Gatt(WSS_GATT_COMPLETION, id, s->connection, s->heldStage); }
    }); });
}
// Read only after a worker barrier. These controls are never exported by production.
__declspec(dllexport) uint32_t WSS_CALL wss_test_nus_observe(wss_ble_context context, uint32_t field, uint32_t index, uint8_t* bytes, uint32_t capacity) {
    auto s = Get(context);
    if (field == 0) return s->resources;
    if (field == 1) return static_cast<uint32_t>(s->writes.size());
    if (field == 2) {
        if (index >= s->writes.size()) return UINT32_MAX;
        auto const& value = s->writes[index];
        if (capacity >= value.size()) std::copy(value.begin(), value.end(), bytes);
        return static_cast<uint32_t>(value.size());
    }
    if (field == 3) {
        if (capacity >= sizeof(s->lastOptions)) std::copy(reinterpret_cast<uint8_t*>(&s->lastOptions), reinterpret_cast<uint8_t*>(&s->lastOptions) + sizeof(s->lastOptions), bytes);
        return sizeof(s->lastOptions);
    }
    if (field == 4) { if (capacity >= s->deviceId.size()) std::copy(s->deviceId.begin(), s->deviceId.end(), bytes); return static_cast<uint32_t>(s->deviceId.size()); }
    return UINT32_MAX;
}
__declspec(dllexport) int32_t WSS_CALL wss_test_nus_budget(wss_ble_context context, uint32_t budget) {
    return Guard([&] { auto s = Get(context); return s->Queue([s, budget] { s->budget = budget; }); });
}
}
