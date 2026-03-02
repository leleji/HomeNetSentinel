module("luci.controller.homenet_sentinel", package.seeall)

function index()
    if not nixio.fs.access("/etc/config/homenet-sentinel") then
        return
    end

    entry({"admin", "services", "homenet-sentinel"}, cbi("homenet_sentinel"), _("家庭网络哨兵"), 60)
    entry({"admin", "services", "homenet-sentinel", "status"}, call("action_status")).leaf = true
end

function action_status()
    local http = require "luci.http"
    local sys = require "luci.sys"

    local running = (sys.call("/etc/init.d/homenet-sentinel status >/dev/null 2>&1") == 0)

    http.prepare_content("application/json")
    http.write_json({ running = running })
end
