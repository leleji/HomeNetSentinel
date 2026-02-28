include $(TOPDIR)/rules.mk

PKG_NAME:=luci-app-homenet-sentinel
PKG_VERSION:=1.0.0
PKG_RELEASE:=1

PKG_BUILD_DIR:=$(BUILD_DIR)/$(PKG_NAME)

include $(INCLUDE_DIR)/package.mk

define Package/luci-app-homenet-sentinel
  SECTION:=luci
  CATEGORY:=LuCI
  SUBMENU:=3. Applications
  TITLE:=LuCI support for HomeNet Sentinel
  DEPENDS:=+libpthread +luci-base
endef

define Package/luci-app-homenet-sentinel/description
 HomeNet Sentinel provides MQTT presence and WAN telemetry reporting with
 LuCI-based configuration on OpenWrt.
endef

define Build/Prepare
	mkdir -p $(PKG_BUILD_DIR)
	if [ ! -f "$(CURDIR)/prebuilt/HomeNet-Sentinel" ]; then \
		echo "Missing prebuilt/HomeNet-Sentinel binary"; \
		echo "Place OpenWrt-compatible binary at package/luci-app-homenet-sentinel/prebuilt/HomeNet-Sentinel"; \
		exit 1; \
	fi
	cp "$(CURDIR)/prebuilt/HomeNet-Sentinel" "$(PKG_BUILD_DIR)/HomeNet-Sentinel"
endef

define Build/Compile
endef

define Package/luci-app-homenet-sentinel/install
	$(INSTALL_DIR) $(1)/usr/bin
	$(INSTALL_BIN) $(PKG_BUILD_DIR)/HomeNet-Sentinel $(1)/usr/bin/HomeNet-Sentinel

	$(INSTALL_DIR) $(1)/etc/init.d
	$(INSTALL_BIN) ./files/etc/init.d/homenet-sentinel $(1)/etc/init.d/homenet-sentinel

	$(INSTALL_DIR) $(1)/etc/config
	$(INSTALL_CONF) ./files/etc/config/homenet-sentinel $(1)/etc/config/homenet-sentinel

	$(INSTALL_DIR) $(1)/usr/lib/lua/luci/controller
	$(INSTALL_DATA) ./files/usr/lib/lua/luci/controller/homenet_sentinel.lua $(1)/usr/lib/lua/luci/controller/homenet_sentinel.lua

	$(INSTALL_DIR) $(1)/usr/lib/lua/luci/model/cbi
	$(INSTALL_DATA) ./files/usr/lib/lua/luci/model/cbi/homenet_sentinel.lua $(1)/usr/lib/lua/luci/model/cbi/homenet_sentinel.lua
endef

define Package/luci-app-homenet-sentinel/postinst
#!/bin/sh
exit 0
endef

$(eval $(call BuildPackage,luci-app-homenet-sentinel))
