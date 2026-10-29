# The game install (Windows build, 1.7): defaults to the nearest folder at or above this one that holds
# Assembly-CSharp.dll or has a RimWorld/ subfolder that does (tools/rimworld-dir.sh).
ifndef RIMWORLD_DIR
RIMWORLD_DIR := $(shell tools/rimworld-dir.sh)
endif
ifeq ($(strip $(RIMWORLD_DIR)),)
ifneq ($(filter-out clean,$(or $(MAKECMDGOALS),build)),)
$(error No RimWorld install found at or above $(CURDIR) (or in a RimWorld/ subfolder of one); set RIMWORLD_DIR=/path/to/RimWorld)
endif
endif
export RIMWORLD_DIR

MOD_DIR  := Mods/RimPrison
MOD_PROJ := $(MOD_DIR)/Source/RimPrison/RimPrison.csproj
DIST_DIR ?= $(CURDIR)/dist

.PHONY: build lint link dist clean

build:
	dotnet build $(MOD_PROJ) -c Release

# Offline def check: every XML field and def reference against the game's real types (no game run needed).
lint: build
	dotnet build tools/deflint -c Release -v quiet -nologo
	dotnet .work/deflint/deflint.dll $(RIMWORLD_DIR) $(MOD_DIR)

# Make the game load this checkout (symlink in $(RIMWORLD_DIR)/Mods; no-op when overlaid on the install).
link:
	tools/link-mod.sh

dist: build
	rm -rf $(DIST_DIR)/RimPrison && mkdir -p $(DIST_DIR)/RimPrison
	# Everything the game loads (About, Assemblies, Defs, Languages, Patches, Textures, ...); not the source.
	for d in $(MOD_DIR)/*/; do [ "$$(basename $$d)" = Source ] || cp -r $$d $(DIST_DIR)/RimPrison/; done
	@for d in About Assemblies Defs Languages Patches Textures; do [ -d $(DIST_DIR)/RimPrison/$$d ] || { echo "dist: missing $$d" >&2; exit 1; }; done
	cd $(DIST_DIR) && rm -f RimPrison.zip && python3 -m zipfile -c RimPrison.zip RimPrison
	@echo "Copy $(DIST_DIR)/RimPrison/ (or unzip RimPrison.zip) into RimWorld/Mods/ on the gaming PC"

clean:
	rm -rf $(MOD_DIR)/Assemblies $(MOD_DIR)/Source/*/bin $(MOD_DIR)/Source/*/obj
