local fs = require "nixio.fs"
local sys = require "luci.sys"

local function get_ifaces()
    local ifaces = {}
    local iter = fs.dir("/sys/class/net")
    if iter then
        for ifname in iter do
            if ifname and ifname ~= "lo" then
                ifaces[#ifaces + 1] = ifname
            end
        end
    end
    table.sort(ifaces)
    return ifaces
end

local function fill_iface_values(opt)
    for _, ifname in ipairs(get_ifaces()) do
        opt:value(ifname)
    end
end

local m = Map("homenet-sentinel", translate("HomeNet Sentinel"), translate("配置 MQTT 在家检测与广域网监控参数。"))

function m.on_after_commit(self)
    local uci = require "luci.model.uci".cursor()
    local enabled = uci:get("homenet-sentinel", "main", "enabled") or "0"

    if enabled == "1" then
        sys.call("/etc/init.d/homenet-sentinel enable >/dev/null 2>&1")
        sys.call("/etc/init.d/homenet-sentinel restart >/dev/null 2>&1")
    else
        sys.call("/etc/init.d/homenet-sentinel stop >/dev/null 2>&1")
        sys.call("/etc/init.d/homenet-sentinel disable >/dev/null 2>&1")
    end
end

local status = m:section(SimpleSection)
status.template = "homenet_sentinel/status"

local s = m:section(TypedSection, "main", translate("基础设置"))
s.anonymous = true
s.addremove = false

local enabled = s:option(Flag, "enabled", translate("启用服务"))
enabled.default = "0"
enabled.rmempty = false

local broker_host = s:option(Value, "broker_host", translate("MQTT 服务器地址"))
broker_host.rmempty = false
broker_host.placeholder = "192.168.5.1"

local broker_port = s:option(Value, "broker_port", translate("MQTT 端口"))
broker_port.rmempty = false
broker_port.datatype = "port"
broker_port.placeholder = "1883"

local username = s:option(Value, "username", translate("MQTT 用户名"))
username.rmempty = false

local password = s:option(Value, "password", translate("MQTT 密码"))
password.password = true
password.rmempty = false

local wan_if = s:option(ListValue, "wan_interface", translate("流量统计网卡"))
fill_iface_values(wan_if)
wan_if.default = "pppoe-wan"
wan_if.rmempty = false

local wan_status_if = s:option(ListValue, "wan_status_interface", translate("WAN 状态接口"))
fill_iface_values(wan_status_if)
wan_status_if:value("wan")
wan_status_if:value("wan6")
wan_status_if:value("wan_6")
wan_status_if.default = "wan"
wan_status_if.rmempty = false

local wan_ipv6_status_if = s:option(ListValue, "wan_ipv6_status_interface", translate("WAN IPv6-PD 接口"))
fill_iface_values(wan_ipv6_status_if)
wan_ipv6_status_if:value("wan")
wan_ipv6_status_if:value("wan6")
wan_ipv6_status_if:value("wan_6")
wan_ipv6_status_if.default = "wan_6"
wan_ipv6_status_if.rmempty = false

local wan_rate_refresh = s:option(Value, "wan_rate_refresh_interval_seconds", translate("上传/下载刷新延迟(秒)"))
wan_rate_refresh.datatype = "uinteger"
wan_rate_refresh.default = "3"
wan_rate_refresh.placeholder = "3"
wan_rate_refresh.rmempty = false

local t = m:section(TypedSection, "target", translate("在家检测目标"), translate("可添加多个目标，名称和 IP 都必须填写。"))
t.template = "cbi/tblsection"
t.anonymous = true
t.addremove = true

local target_name = t:option(Value, "name", translate("名称"))
target_name.rmempty = false

local target_ip = t:option(Value, "ip", translate("IP 地址"))
target_ip.datatype = "ipaddr"
target_ip.rmempty = false

return m
