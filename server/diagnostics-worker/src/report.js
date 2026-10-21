export const MAX_REPORT_BYTES = 50 * 1024;

const protocols = new Set([
  "Logitech HID++", "MCHOSE", "LANGTU LT95", "LANGTU Shinetek", "Weisheng",
  "Razer", "Compx", "AULA", "Akko", "DualSense", "Steam", "SteelSeries Nova",
  "Corsair VOID", "HyperX Cloud", "HID 标准电池", "BLE Battery Service", "unknown",
]);
const statuses = new Set([
  "descriptor_mismatch", "missing_collection", "access_failed", "invalid_response",
  "timeout", "no_response", "offline", "no_standard_battery", "success", "unknown",
]);
const categories = new Set(["键盘", "鼠标", "耳机", "麦克风", "音频设备", "手柄", "外设"]);
const connections = new Set(["bluetooth", "receiver", "usb", "unknown"]);
const levels = new Set(["极低", "低", "中", "高", "满", "未知", "已耗尽", "低电量", "中等", "充足", "满电"]);
const alias = /^device-[1-9]\d{0,3}$/;
const hex2 = /^[0-9A-F]{2}$/;
const hex4 = /^[0-9A-F]{4}$/;

export class RequestError extends Error {
  constructor(status, code, retryAfter) {
    super(code);
    this.status = status;
    this.retryAfter = retryAfter;
  }
}

function invalid() { throw new RequestError(400, "invalid_report"); }
function object(value, keys) {
  if (!value || typeof value !== "object" || Array.isArray(value)) invalid();
  if (Object.keys(value).some(key => !keys.includes(key))) invalid();
  return value;
}
function string(value, pattern) {
  if (typeof value !== "string" || !pattern.test(value)) invalid();
  return value;
}
function member(value, allowed) {
  if (!allowed.has(value)) invalid();
  return value;
}
function boolean(value) {
  if (typeof value !== "boolean") invalid();
  return value;
}
function integer(value, min, max) {
  if (!Number.isSafeInteger(value) || value < min || value > max) invalid();
  return value;
}
function optional(target, source, key, validate) {
  if (source[key] !== undefined) target[key] = validate(source[key]);
}
function array(value, max, validate) {
  if (!Array.isArray(value) || value.length > max) invalid();
  return value.map(validate);
}
function status(value) {
  if (!statuses.has(value) && !(typeof value === "string" && /^device_error_[0-9A-F]{2}$/.test(value))) invalid();
  return value;
}
function batteryLevel(value) {
  if (levels.has(value)) return value;
  if (typeof value !== "string") invalid();
  if (/^档位 [0-4]\/4$/.test(value)) return value;
  if (/^(?:0|[1-9]0)–(?:9|[1-9]9)%$/.test(value)) {
    const [low, high] = value.slice(0, -1).split("–").map(Number);
    if (high === low + 9) return value;
  }
  invalid();
}

function device(value) {
  const source = object(value, ["id", "receiverId", "vendorId", "productId", "category", "connection",
    "connectionKnown", "isConnected", "batteryPercent", "batteryLevel", "isCharging",
    "hasProtocolConnectionState", "protocol", "status", "batteryReadFailed"]);
  const result = {
    id: string(source.id, alias), connection: member(source.connection, connections),
    connectionKnown: boolean(source.connectionKnown), isConnected: boolean(source.isConnected),
    hasProtocolConnectionState: boolean(source.hasProtocolConnectionState),
    protocol: member(source.protocol, protocols), status: status(source.status),
    batteryReadFailed: boolean(source.batteryReadFailed),
  };
  optional(result, source, "receiverId", v => string(v, alias));
  optional(result, source, "vendorId", v => string(v, hex4));
  optional(result, source, "productId", v => string(v, hex4));
  optional(result, source, "category", v => member(v, categories));
  optional(result, source, "batteryPercent", v => integer(v, 0, 100));
  optional(result, source, "batteryLevel", batteryLevel);
  optional(result, source, "isCharging", boolean);
  if (result.batteryReadFailed && (!result.connectionKnown || !result.isConnected ||
      result.batteryPercent !== undefined || (result.batteryLevel !== undefined && result.batteryLevel !== "未知"))) invalid();
  return result;
}

function batteryQuery(value, features) {
  const source = object(value, ["slot", "feature", "function", "arguments", "error", "response"]);
  const result = {
    slot: integer(source.slot, 1, 6), feature: string(source.feature, hex2),
    function: member(source.function, new Set(["0", "1"])), arguments: string(source.arguments, hex4),
  };
  optional(result, source, "error", v => string(v, hex2));
  optional(result, source, "response", v => string(v, /^[0-9A-F]{14}$/));
  const discovery = result.feature === "00" && result.function === "0" && ["1004", "1000"].includes(result.arguments);
  const knownFeature = features.get(`${result.slot}:${result.feature}`);
  if (!discovery && !(result.arguments === "0000" && knownFeature && (result.function === "0" || knownFeature === "1004"))) invalid();
  if (result.error && !result.response) invalid();
  if (result.response) {
    const bytes = result.response.match(/../g).map(value => Number.parseInt(value, 16));
    const requestedFeature = Number.parseInt(result.feature, 16);
    const requestedFunction = Number(result.function);
    if (bytes[1] !== result.slot) invalid();
    if (result.error) {
      if (!((bytes[0] === 0x10 && bytes[2] === 0x8f) || (bytes[0] === 0x11 && bytes[2] === 0xff)) ||
          bytes[3] !== requestedFeature || bytes[4] >> 4 !== requestedFunction || !(bytes[4] & 15) ||
          bytes[5] !== Number.parseInt(result.error, 16) || bytes[6] !== 0) invalid();
    } else {
      if (![0x10, 0x11].includes(bytes[0]) || bytes[2] !== requestedFeature ||
          bytes[3] >> 4 !== requestedFunction || !(bytes[3] & 15)) invalid();
      if (discovery && bytes[4] > 0 && bytes[4] < 255)
        features.set(`${result.slot}:${bytes[4].toString(16).padStart(2, "0").toUpperCase()}`, result.arguments);
    }
  }
  return result;
}

function hidInterface(value) {
  const source = object(value, ["deviceId", "vendorId", "productId", "interface", "collection", "usage",
    "inputBytes", "outputBytes", "featureBytes", "inputReports", "outputReports", "featureReports",
    "protocol", "status", "windowsError", "batteryQueries", "batteryPrefix"]);
  const result = {
    deviceId: string(source.deviceId, alias), vendorId: string(source.vendorId, hex4),
    productId: string(source.productId, hex4), protocol: member(source.protocol, protocols), status: status(source.status),
  };
  for (const key of ["interface", "collection"]) optional(result, source, key, v => string(v, hex2));
  optional(result, source, "usage", v => string(v, /^[0-9A-F]{4}\/[0-9A-F]{4}$/));
  for (const key of ["inputBytes", "outputBytes", "featureBytes"])
    optional(result, source, key, v => integer(v, 0, 65535));
  optional(result, source, "windowsError", v => integer(v, 0, 99999));
  for (const key of ["inputReports", "outputReports", "featureReports"])
    optional(result, source, key, v => array(v, 32, item => string(item, /^[0-9A-F]{2}:(?:\d{1,3}X\d{1,5}|BUTTONS)$/)));
  optional(result, source, "batteryQueries", v => {
    if (result.protocol !== "Logitech HID++" || result.vendorId !== "046D") invalid();
    const features = new Map();
    return array(v, 40, query => batteryQuery(query, features));
  });
  optional(result, source, "batteryPrefix", v => {
    const pattern = result.protocol === "MCHOSE" ? /^(?:5565[0-9A-F]{4}|55AA01(?:00|01)0300010B[0-9A-F]{4})$/
      : result.protocol === "LANGTU LT95" ? /^00200100[0-9A-F]{2}$/
      : result.protocol === "Akko" ? /^0400001A06000000[0-9A-F]{4}$/ : /$a/;
    return string(v, pattern);
  });
  return result;
}

// Reconstruct every object; only these enumerated/typed fields can reach GitHub.
export function sanitizeReport(value) {
  const source = object(value, ["schemaVersion", "applicationVersion", "windowsVersion", "failedDeviceCount",
    "devices", "relatedDevices", "interfaces", "omittedDevices", "omittedDiagnosticLines"]);
  if (source.schemaVersion !== 1) invalid();
  const result = {
    schemaVersion: 1,
    applicationVersion: string(source.applicationVersion, /^(?:unknown|\d{1,5}(?:\.\d{1,5}){1,3}(?:-[A-Za-z0-9.-]{1,32})?)$/),
    windowsVersion: string(source.windowsVersion, /^\d{1,5}(?:\.\d{1,5}){1,3}$/),
    failedDeviceCount: integer(source.failedDeviceCount, 0, 1000000),
    devices: array(source.devices, 64, device),
    relatedDevices: array(source.relatedDevices, 192, value => ({ id: string(object(value, ["id"]).id, alias) })),
    interfaces: array(source.interfaces, 128, hidInterface),
    omittedDevices: integer(source.omittedDevices, 0, 1000000),
    omittedDiagnosticLines: integer(source.omittedDiagnosticLines, 0, 1000000),
  };
  const ids = result.devices.map(item => item.id).concat(result.relatedDevices.map(item => item.id));
  if (new Set(ids).size !== ids.length) invalid();
  const knownIds = new Set(ids);
  if (result.devices.some(item => item.receiverId && !knownIds.has(item.receiverId)) ||
      result.interfaces.some(item => !knownIds.has(item.deviceId))) invalid();
  const includedFailures = result.devices.filter(item => item.batteryReadFailed).length;
  if (result.failedDeviceCount < includedFailures || result.failedDeviceCount > includedFailures + result.omittedDevices) invalid();
  return result;
}

export async function sha256(value) {
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return Array.from(new Uint8Array(bytes), byte => byte.toString(16).padStart(2, "0")).join("");
}

export async function fingerprint(report) {
  let focus = report.devices.filter(device => device.batteryReadFailed);
  const mode = focus.length ? "failure" : "manual";
  if (!focus.length) focus = report.devices;
  const keys = focus.map(device => [device.vendorId ?? "-", device.productId ?? "-", device.category ?? "-",
    device.connection, device.protocol, device.status,
    device.batteryPercent !== undefined ? "percent" : device.batteryLevel && device.batteryLevel !== "未知" ? "level" : "none"].join("|"));
  return sha256(`1\n${report.applicationVersion}\n${mode}\n${[...new Set(keys)].sort().join("\n")}`);
}

export function issueContent(report, hash) {
  const focus = report.devices.find(device => device.batteryReadFailed) ?? report.devices[0];
  const hardware = focus ? `${focus.vendorId ?? "????"}:${focus.productId ?? "????"}` : "未发现设备";
  return {
    title: `[诊断] ${report.failedDeviceCount ? "电量读取失败" : "手动报告"} ${hardware} · ${report.applicationVersion}`,
    body: "由 Perivolt 诊断接收服务提交。字段经过白名单验证；不含设备名称、序列号、路径、原始错误或 IP。\n\n" +
      `诊断指纹：\`${hash}\`\n\n` + "```json\n" + JSON.stringify(report) + "\n```",
  };
}
