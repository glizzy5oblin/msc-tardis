# MSC-TARDIS: build the mod from source. A plain Makefile for anyone with the .NET SDK and My Summer Car + MSCLoader.
#
#   make setup                       check the tools and the game, restore packages
#   make build                       compile to src/bin/Release/net35/MscTardis.dll
#   make build DEPLOY=1              compile and copy the dll and assets into the game's Mods folder
#   make setup GAME="D:/Games/My Summer Car"      use a game folder other than the default (works on every target)

# The game folder: forward slashes. Override on the command line or in the environment.
GAME    ?= C:/Program Files (x86)/Steam/steamapps/common/My Summer Car
MANAGED := $(GAME)/mysummercar_Data/Managed
MODS    := $(GAME)/Mods
SRC     := src
null    :=
space   := $(null) $(null)
# wildcard splits on spaces, so a game folder with spaces needs them escaped for the checks below
GAMEESC := $(subst $(space),\$(space),$(GAME))
DEPLOY  ?=
PROPS   := -p:"Managed=$(MANAGED)" -p:"GameMods=$(MODS)" $(if $(DEPLOY),-p:Deploy=true)

.DEFAULT_GOAL := help
.PHONY: help setup build clean

help:
	@echo "MSC-TARDIS - build from source"
	@echo "  make setup   Check dotnet and the game, restore packages"
	@echo "  make build   Compile the mod to $(SRC)/bin/Release/net35/MscTardis.dll"
	@echo "  make clean   Remove build output"
	@echo "  make help    Show this help"
	@echo "Options:"
	@echo "  GAME=path    My Summer Car folder, with forward slashes. Default: $(GAME)"
	@echo "  DEPLOY=1     With build: also copy the dll and assets into the Mods folder of the game"
	@echo "Needs: .NET SDK, My Summer Car, MSCLoader installed in the game."

setup:
	@dotnet --version
	$(if $(wildcard $(GAMEESC)/mysummercar.exe),@echo game: $(GAME),$(error My Summer Car not found at "$(GAME)": pass its folder, e.g. make setup GAME="D:/Games/My Summer Car"))
	$(if $(wildcard $(GAMEESC)/mysummercar_Data/Managed/MSCLoader.dll),@echo MSCLoader: found,$(error MSCLoader is not installed in the game: $(MANAGED)/MSCLoader.dll is missing))
	dotnet restore $(SRC)

build: setup
	dotnet build $(SRC) -c Release $(PROPS)

clean:
	dotnet clean $(SRC) -c Release $(PROPS)
