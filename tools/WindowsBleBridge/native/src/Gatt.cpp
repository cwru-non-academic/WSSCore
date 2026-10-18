#include <Windows.h>
#include "BleContext.h"
#include <algorithm>
#include <array>
#include <climits>

using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::Devices::Bluetooth;
using namespace Windows::Devices::Bluetooth::GenericAttributeProfile;
using namespace Windows::Storage::Streams;
namespace wss {
namespace {
guid Uuid(uint8_t const* b) {
    return guid{ (uint32_t(b[0]) << 24) | (uint32_t(b[1]) << 16) | (uint32_t(b[2]) << 8) | b[3],
        uint16_t((uint16_t(b[4]) << 8) | b[5]), uint16_t((uint16_t(b[6]) << 8) | b[7]),
        { b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15] } };
}
void CloseAsync(IAsyncInfo& async) noexcept {
    try { if (async) { async.Cancel(); async.Close(); } } catch (...) {}
    async = nullptr;
}
uint32_t Protocol(IReference<uint8_t> const& error) { return error ? error.Value() : UINT32_MAX; }
}
int32_t BleContext::SubmitGatt(Command command, uint64_t id, uint64_t connection,
    wss_ble_nus_connect_options const* options, uint8_t const* bytes, uint32_t length, uint32_t timeout) {
    if (!timeout || timeout > INT32_MAX || (length && !bytes) || length > UINT16_MAX - 3u) return WSS_INVALID_ARGUMENT;
    auto op = std::make_shared<Operation>();
    op->id = id; op->connection = connection; op->gatt = true; op->command = command;
    op->deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout);
    if (command == Command::Connect) {
        if (!options || options->struct_size != sizeof(*options) || options->reserved ||
            options->selector_kind < 1 || options->selector_kind > 2 || options->address_type > 2 ||
            Uuid(options->service_uuid) == guid{} || Uuid(options->rx_uuid) == guid{} || Uuid(options->tx_uuid) == guid{}) return WSS_INVALID_ARGUMENT;
        if (options->selector_kind == 1 && (!options->address || options->address > 0xffffffffffffULL)) return WSS_INVALID_ARGUMENT;
        if (options->selector_kind == 2) {
            if (!options->device_id_utf8 || !options->device_id_length || options->device_id_length > 65536) return WSS_INVALID_ARGUMENT;
            op->deviceId.assign(reinterpret_cast<char const*>(options->device_id_utf8), options->device_id_length);
            if (op->deviceId.find('\0') != std::string::npos) return WSS_INVALID_ARGUMENT;
            // MultiByteToWideChar validates UTF-8 strictly; WinRT's conversion is then safe.
            if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, op->deviceId.data(), static_cast<int>(op->deviceId.size()), nullptr, 0)) return WSS_INVALID_ARGUMENT;
        }
        op->options = *options; op->options.device_id_utf8 = nullptr;
    }
    if (length) op->bytes.assign(bytes, bytes + length);
    std::lock_guard<std::mutex> lock(gate_);
    if (closing_) return WSS_CLOSING;
    if (operations_.count(id)) return WSS_INVALID_ARGUMENT;
    if (operations_.size() >= 128) return WSS_BUSY;
    commands_.push_back([this, op] { ExecuteGatt(op); });
    try { operations_.emplace(id, op); } catch (...) { commands_.pop_back(); throw; }
    wake_.notify_one(); return WSS_OK;
}
void BleContext::AwaitGatt(std::shared_ptr<Operation> const& op, IAsyncInfo const& async) {
    op->async = async;
    op->gattStatus = op->protocolError = UINT32_MAX;
    // Typed completion delegates are attached by each stage; all dispatch to MarkReady.
}
void BleContext::ExecuteGatt(std::shared_ptr<Operation> const& op) {
    if (!Active(op)) return;
    bool cancelled;
    { std::lock_guard<std::mutex> lock(gate_); cancelled = op->cancelled; }
    if (cancelled && op->command != Command::Disconnect) { FinishGatt(op, WSS_CANCELLED); return; }
    if (std::chrono::steady_clock::now() >= op->deadline && op->command != Command::Disconnect) { FinishGatt(op, WSS_TIMED_OUT); return; }
    try {
        if (apartmentError_ || !initialized_) { FinishGatt(op, apartmentError_ ? WSS_NATIVE_FAILURE : WSS_INVALID_STATE, apartmentError_); return; }
        auto weak = weak_from_this();
        auto done = [weak, op](auto const&, AsyncStatus) noexcept { if (auto self = weak.lock()) self->MarkReady(op); };
        if (op->command == Command::Connect) {
            op->stage = WSS_RESOLVE_DEVICE;
            if (retiredWrite_) { FinishGatt(op, WSS_BUSY); return; }
            if (connection_ || op->connection <= lastConnection_) { FinishGatt(op, WSS_INVALID_STATE); return; }
            connection_ = std::make_shared<Connection>();
            op->ownsConnection = true;
            connection_->id = lastConnection_ = op->connection;
            { std::lock_guard<std::mutex> lock(gate_); publishedConnection_ = op->connection; }
            IAsyncOperation<BluetoothLEDevice> async{ nullptr };
            if (op->options.selector_kind == 2) async = BluetoothLEDevice::FromIdAsync(to_hstring(op->deviceId));
            else if (op->options.address_type == WSS_ADDRESS_UNSPECIFIED) async = BluetoothLEDevice::FromBluetoothAddressAsync(op->options.address);
            else async = BluetoothLEDevice::FromBluetoothAddressAsync(op->options.address,
                op->options.address_type == WSS_ADDRESS_PUBLIC ? BluetoothAddressType::Public : BluetoothAddressType::Random);
            AwaitGatt(op, async); async.Completed(done);
        } else if (op->command == Command::Disconnect) {
            op->stage = WSS_DISABLE_NOTIFY;
            if (!connection_) { FinishGatt(op, WSS_OK); return; }
            if (connection_->id != op->connection) { FinishGatt(op, WSS_INVALID_STATE); return; }
            op->finishing = true; op->result = cancelled ? WSS_CANCELLED : WSS_OK;
            if (connection_->cleaning) connection_->cleanupDeadline = std::min(connection_->cleanupDeadline, op->deadline);
            BeginCleanup(false);
        } else {
            op->stage = WSS_WRITE;
            if (!connection_ || !connection_->ready || connection_->id != op->connection || connection_->cleaning) { FinishGatt(op, WSS_INVALID_STATE); return; }
            if (writeOperation_ || retiredWrite_) { FinishGatt(op, WSS_BUSY); return; }
            if (op->bytes.size() > WriteBudget()) { FinishGatt(op, WSS_INVALID_ARGUMENT); return; }
            DataWriter writer;
            writer.WriteBytes(op->bytes);
            auto buffer = writer.DetachBuffer(); writer.Close();
            auto async = connection_->rx.WriteValueWithResultAsync(buffer, GattWriteOption::WriteWithResponse);
            writeOperation_ = op->id;
            AwaitGatt(op, async); async.Completed(done);
        }
    } catch (...) {
        op->hr = to_hresult().value;
        if (connection_ && connection_->id == op->connection) {
            op->finishing = true; op->result = ExceptionStatus();
            BeginCleanup(op->command == Command::Write, op->result, op->stage, op->hr);
        } else FinishGatt(op, ExceptionStatus(), op->hr);
    }
}
void BleContext::ProcessGatt(std::shared_ptr<Operation> const& op) {
    if (!Active(op) || op->finishing) return;
    if (!connection_ || connection_->id != op->connection || connection_->cleaning) { FinishGatt(op, WSS_INVALID_STATE); return; }
    try {
        auto c = connection_;
        auto weak = weak_from_this();
        auto done = [weak, op](auto const&, AsyncStatus) noexcept { if (auto self = weak.lock()) self->MarkReady(op); };
        if (op->stage == WSS_RESOLVE_DEVICE) {
            c->device = op->async.as<IAsyncOperation<BluetoothLEDevice>>().GetResults();
            if (!c->device) throw hresult_error(E_FAIL);
            c->sawConnected = c->device.ConnectionStatus() == BluetoothConnectionStatus::Connected;
            c->statusToken = c->device.ConnectionStatusChanged([weak, id = c->id](auto const& device, auto const&) noexcept {
                try { if (auto self = weak.lock()) self->LinkChanged(id, device.ConnectionStatus() == BluetoothConnectionStatus::Connected); } catch (...) {}
            }); c->statusAttached = true;
            op->stage = WSS_DISCOVER_SERVICE;
            auto async = c->device.GetGattServicesForUuidAsync(Uuid(op->options.service_uuid), BluetoothCacheMode::Uncached);
            AwaitGatt(op, async); async.Completed(done);
        } else if (op->stage == WSS_DISCOVER_SERVICE) {
            auto result = op->async.as<IAsyncOperation<GattDeviceServicesResult>>().GetResults();
            op->gattStatus = static_cast<uint32_t>(result.Status()); op->protocolError = Protocol(result.ProtocolError());
            for (auto const& service : result.Services()) {
                if (!c->service && result.Status() == GattCommunicationStatus::Success) c->service = service;
                else service.Close();
            }
            if (result.Status() != GattCommunicationStatus::Success || !c->service) throw hresult_error(E_FAIL);
            c->session = c->service.Session();
            op->stage = WSS_DISCOVER_RX;
            auto async = c->service.GetCharacteristicsForUuidAsync(Uuid(op->options.rx_uuid), BluetoothCacheMode::Uncached);
            AwaitGatt(op, async); async.Completed(done);
        } else if (op->stage == WSS_DISCOVER_RX || op->stage == WSS_DISCOVER_TX) {
            auto result = op->async.as<IAsyncOperation<GattCharacteristicsResult>>().GetResults();
            op->gattStatus = static_cast<uint32_t>(result.Status()); op->protocolError = Protocol(result.ProtocolError());
            auto required = op->stage == WSS_DISCOVER_RX ? GattCharacteristicProperties::Write : GattCharacteristicProperties::Notify;
            GattCharacteristic selected{ nullptr };
            for (auto const& item : result.Characteristics()) if (!selected && (item.CharacteristicProperties() & required) != GattCharacteristicProperties::None) selected = item;
            if (result.Status() != GattCommunicationStatus::Success || !selected) throw hresult_error(E_FAIL);
            if (op->stage == WSS_DISCOVER_RX) {
                c->rx = selected; op->stage = WSS_DISCOVER_TX;
                auto async = c->service.GetCharacteristicsForUuidAsync(Uuid(op->options.tx_uuid), BluetoothCacheMode::Uncached);
                AwaitGatt(op, async); async.Completed(done);
            } else {
                c->tx = selected;
                c->valueToken = c->tx.ValueChanged([weak, id = c->id](auto const&, auto const& args) noexcept {
                    if (auto self = weak.lock()) {
                        try { self->Notify(id, args.CharacteristicValue()); }
                        catch (...) { self->Notify(id, nullptr); } // fail the stream rather than silently lose a value
                    }
                }); c->notifyAttached = true;
                op->stage = WSS_ENABLE_NOTIFY;
                auto async = c->tx.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue::Notify);
                AwaitGatt(op, async); async.Completed(done);
            }
        } else {
            auto result = op->async.as<IAsyncOperation<GattWriteResult>>().GetResults();
            op->gattStatus = static_cast<uint32_t>(result.Status()); op->protocolError = Protocol(result.ProtocolError());
            if (result.Status() != GattCommunicationStatus::Success) {
                if (op->stage == WSS_WRITE) { FinishGatt(op, WSS_NATIVE_FAILURE); return; }
                throw hresult_error(E_FAIL);
            }
            if (op->stage == WSS_ENABLE_NOTIFY) {
                if (c->device.ConnectionStatus() != BluetoothConnectionStatus::Connected) throw hresult_error(E_FAIL);
                c->ready = c->sawConnected = true;
                if (!WriteBudget()) throw hresult_error(E_FAIL);
            }
            FinishGatt(op, WSS_OK);
        }
    } catch (...) {
        op->hr = to_hresult().value; op->result = ExceptionStatus(); op->finishing = true;
        BeginCleanup(op->command == Command::Write, op->result, op->stage, op->hr);
    }
}
uint32_t BleContext::WriteBudget() const {
    if (!connection_ || !connection_->ready || !connection_->session) return 0;
    // Callback publication must not throw after removing an operation's reserved slot.
    try { auto mtu = connection_->session.MaxPduSize(); return mtu > 3 ? mtu - 3u : 0u; }
    catch (...) { return 0; }
}
void BleContext::EmitGatt(uint32_t kind, uint64_t operation, uint64_t connection, int32_t status,
    uint32_t stage, uint32_t gattStatus, uint32_t protocolError, int32_t hr, std::vector<uint8_t> const* data) {
    wss_ble_gatt_event event{};
    event.base = { sizeof(event), kind, operation, connection, status, hr, 0, 0, nullptr, 0 };
    event.stage = stage; event.gatt_status = gattStatus; event.protocol_error = protocolError;
    event.max_write_length = WriteBudget();
    if (data) { event.data = data->data(); event.data_length = static_cast<uint32_t>(data->size()); }
    Emit(event.base);
}
void BleContext::FinishGatt(std::shared_ptr<Operation> const& op, int32_t status, int32_t hr) {
    bool cancelCleanup = false;
    {
        std::lock_guard<std::mutex> lock(gate_);
        auto found = operations_.find(op->id);
        if (closing_ || found == operations_.end() || found->second != op) return;
        if (op->cancelled) {
            cancelCleanup = !op->finishing && connection_ && connection_->id == op->connection &&
                ((op->command == Command::Connect && op->ownsConnection) || (op->command == Command::Write && writeOperation_ == op->id));
            status = WSS_CANCELLED;
        }
        else if (op->command == Command::Disconnect && status == WSS_OK && std::chrono::steady_clock::now() >= op->deadline) {
            // Shared cleanup may have finished before this queued disconnect ran.
            // Enforce its own deadline at completion commitment, including the
            // already-disconnected path that does not enter FinishCleanup.
            status = WSS_TIMED_OUT;
        }
        if (!cancelCleanup) operations_.erase(found);
    }
    if (cancelCleanup) { AbortGatt(op, WSS_CANCELLED); return; }
    op->async = nullptr;
    if (writeOperation_ == op->id) writeOperation_ = 0;
    EmitGatt(WSS_GATT_COMPLETION, op->id, op->connection, status, op->stage, op->gattStatus, op->protocolError, hr ? hr : op->hr);
}
void BleContext::AbortGatt(std::shared_ptr<Operation> const& op, int32_t status) {
    if (op->finishing) return;
    if (!op->stage) { FinishGatt(op, status); return; }
    op->finishing = true; op->result = status;
    if (op->command == Command::Write && writeOperation_ == op->id && op->async) {
        retiredWrite_ = op->async;
        try { retiredWrite_.Cancel(); } catch (...) {}
        op->async = nullptr; writeOperation_ = 0;
    } else CloseAsync(op->async);
    if (connection_ && connection_->id == op->connection) BeginCleanup(op->command == Command::Write, status, op->stage);
    else FinishGatt(op, status);
}
void BleContext::BeginCleanup(bool terminal, int32_t status, uint32_t stage, int32_t hr) {
    auto c = connection_; if (!c) return;
    if (terminal && !c->terminal) { c->terminal = true; c->terminalStatus = status; c->terminalStage = stage; c->terminalHr = hr; }
    if (c->cleaning) return;
    c->cleaning = true; c->ready = false;
    {
        std::lock_guard<std::mutex> lock(gate_);
        publishedConnection_ = 0; notifications_.clear(); notificationBytes_ = 0;
        linkPending_ = sawConnectedPending_ = notificationOverflow_ = false;
        for (auto const& pair : operations_) if (pair.second->gatt && pair.second->connection == c->id) {
            auto op = pair.second;
            if (!op->finishing) { op->finishing = true; op->result = WSS_CANCELLED; }
            if (writeOperation_ == op->id && op->async) { retiredWrite_ = op->async; op->async = nullptr; writeOperation_ = 0; }
        }
    }
    try { if (retiredWrite_) retiredWrite_.Cancel(); } catch (...) {}
    // All mutation and revocation happens on the MTA, never at the WinRT callback boundary.
    try { if (c->notifyAttached) c->tx.ValueChanged(c->valueToken); } catch (...) {}
    try { if (c->statusAttached) c->device.ConnectionStatusChanged(c->statusToken); } catch (...) {}
    c->notifyAttached = c->statusAttached = false;
    std::array<std::shared_ptr<Operation>, 128> pending{}; size_t pendingCount = 0;
    { std::lock_guard<std::mutex> lock(gate_); for (auto const& p : operations_) if (p.second->gatt && p.second->connection == c->id) pending[pendingCount++] = p.second; }
    c->cleanupDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    for (size_t i = 0; i < pendingCount; ++i) { auto const& op = pending[i]; CloseAsync(op->async); c->cleanupDeadline = std::min(c->cleanupDeadline, op->deadline); }
    try {
        if (c->tx && c->notifyAttached == false) c->cleanup = c->tx.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue::None);
    } catch (...) { c->cleanupHr = to_hresult().value; }
}
void BleContext::FinishCleanup() {
    auto c = connection_; if (!c) return;
    uint32_t cleanupStatus = UINT32_MAX, protocol = UINT32_MAX;
    try {
        if (c->cleanup && c->cleanup.Status() != AsyncStatus::Started) {
            auto result = c->cleanup.as<IAsyncOperation<GattWriteResult>>().GetResults();
            cleanupStatus = static_cast<uint32_t>(result.Status()); protocol = Protocol(result.ProtocolError());
        }
    } catch (...) { c->cleanupHr = to_hresult().value; }
    CloseAsync(c->cleanup);
    c->rx = c->tx = nullptr;
    try { if (c->session) c->session.Close(); } catch (...) {} c->session = nullptr;
    try { if (c->service) c->service.Close(); } catch (...) {} c->service = nullptr;
    try { if (c->device) c->device.Close(); } catch (...) {} c->device = nullptr;
    connection_.reset();
    std::array<std::shared_ptr<Operation>, 128> finished{}; size_t finishedCount = 0;
    { std::lock_guard<std::mutex> lock(gate_); for (auto const& p : operations_) if (p.second->gatt && p.second->connection == c->id && p.second->finishing) finished[finishedCount++] = p.second; }
    for (size_t i = 0; i < finishedCount; ++i) {
        auto const& op = finished[i];
        if (op->command == Command::Disconnect) {
            op->gattStatus = cleanupStatus; op->protocolError = protocol;
            op->hr = c->cleanupHr;
            if (std::chrono::steady_clock::now() >= op->deadline) op->result = WSS_TIMED_OUT;
        }
        FinishGatt(op, op->result);
    }
    if (c->terminal) EmitGatt(WSS_CONNECTION_TERMINATED, 0, c->id, c->terminalStatus, c->terminalStage, UINT32_MAX, UINT32_MAX, c->terminalHr);
}
void BleContext::LinkChanged(uint64_t connection, bool connected) noexcept {
    try {
        std::lock_guard<std::mutex> lock(gate_);
        if (closing_ || publishedConnection_ != connection) return;
        // Do not coalesce away a disconnect behind a later connect.
        if (!linkPending_ || !connected) linkConnected_ = connected;
        sawConnectedPending_ = sawConnectedPending_ || connected;
        linkPending_ = true; wake_.notify_one();
    } catch (...) {}
}
void BleContext::Notify(uint64_t connection, IBuffer const& buffer) noexcept {
    try {
        if (!buffer.Length()) return;
        Notification item{ connection, {} };
        if (buffer.Length() > 1024 * 1024) {
            std::lock_guard<std::mutex> lock(gate_);
            if (!closing_ && publishedConnection_ == connection) { notificationOverflow_ = true; wake_.notify_one(); }
            return;
        }
        // Serialize copies with enqueueing to preserve callback-observed order.
        std::lock_guard<std::mutex> lock(gate_);
        if (closing_ || publishedConnection_ != connection) return;
        if (notifications_.size() >= 256 || notificationBytes_ + buffer.Length() > 1024 * 1024) notificationOverflow_ = true;
        else {
            item.bytes.resize(buffer.Length()); auto reader = DataReader::FromBuffer(buffer); reader.ReadBytes(item.bytes); reader.Close();
            notificationBytes_ += item.bytes.size(); notifications_.push_back(std::move(item));
        }
        wake_.notify_one();
    } catch (...) {
        try { std::lock_guard<std::mutex> lock(gate_); if (!closing_ && publishedConnection_ == connection) { notificationOverflow_ = true; wake_.notify_one(); } } catch (...) {}
    }
}
void BleContext::TickGatt() {
    if (retiredWrite_) {
        try { if (retiredWrite_.Status() != AsyncStatus::Started) CloseAsync(retiredWrite_); }
        catch (...) { /* Unknown is not terminal: retain the fence until destroy. */ }
    }
    bool link = false, connected = false, sawConnected = false, overflow = false;
    std::array<std::shared_ptr<Operation>, 128> expired{}; size_t expiredCount = 0;
    auto now = std::chrono::steady_clock::now();
    {
        std::lock_guard<std::mutex> lock(gate_);
        link = linkPending_; connected = linkConnected_; overflow = notificationOverflow_;
        sawConnected = sawConnectedPending_;
        linkPending_ = sawConnectedPending_ = notificationOverflow_ = false;
        // Queued commands retain their reserved slot until ExecuteGatt observes the
        // deadline. Removing them here would leave unbounded stale command closures
        // under a flood of short-deadline submissions.
        for (auto const& p : operations_) if (p.second->gatt && p.second->stage && !p.second->finishing && now >= p.second->deadline) expired[expiredCount++] = p.second;
    }
    if (connection_) {
        if (overflow) BeginCleanup(true, WSS_OVERFLOW, WSS_ENABLE_NOTIFY);
        else if (link && !connection_->cleaning) {
            connection_->sawConnected = connection_->sawConnected || sawConnected;
            if (connected) connection_->sawConnected = true;
            else if (connection_->ready || connection_->sawConnected) BeginCleanup(true);
        }
    }
    for (size_t i = 0; i < expiredCount; ++i) AbortGatt(expired[i], WSS_TIMED_OUT);
    if (connection_ && connection_->cleaning) {
        bool done = !connection_->cleanup || now >= connection_->cleanupDeadline;
        try { if (connection_->cleanup && connection_->cleanup.Status() != AsyncStatus::Started) done = true; } catch (...) { done = true; }
        if (done) FinishCleanup();
    }
    Notification item{}; bool emit = false;
    {
        std::lock_guard<std::mutex> lock(gate_);
        if (connection_ && connection_->ready && !notifications_.empty()) {
            item = std::move(notifications_.front()); notifications_.pop_front(); notificationBytes_ -= item.bytes.size(); emit = true;
        }
    }
    if (emit && connection_ && item.connection == connection_->id) EmitGatt(WSS_NOTIFICATION, 0, item.connection, WSS_OK, WSS_ENABLE_NOTIFY, UINT32_MAX, UINT32_MAX, 0, &item.bytes);
}
void BleContext::ShutdownGatt() {
    // Allocation-free shutdown: never launch CCCD work or wait for the OS here.
    auto c = connection_;
    if (c) {
        try { if (c->notifyAttached) c->tx.ValueChanged(c->valueToken); } catch (...) {}
        try { if (c->statusAttached) c->device.ConnectionStatusChanged(c->statusToken); } catch (...) {}
        CloseAsync(c->cleanup); c->rx = c->tx = nullptr;
        try { if (c->session) c->session.Close(); } catch (...) {} c->session = nullptr;
        try { if (c->service) c->service.Close(); } catch (...) {} c->service = nullptr;
        try { if (c->device) c->device.Close(); } catch (...) {} c->device = nullptr;
        connection_.reset();
    }
    { std::lock_guard<std::mutex> lock(gate_); publishedConnection_ = 0; notifications_.clear(); notificationBytes_ = 0; }
    CloseAsync(retiredWrite_);
}
}
