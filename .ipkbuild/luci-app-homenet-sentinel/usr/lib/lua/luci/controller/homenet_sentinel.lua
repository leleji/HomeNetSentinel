module("luci.controller.homenet_sentinel", package.seeall)

function index()
    if not nixio.fs.access("/etc/config/homenet-sentinel") then
        return
    end

    entry({"admin", "services", "homenet-sentinel"}, cbi("homenet_sentinel"), _("家庭网络哨兵"), 60)
end
