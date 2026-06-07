using Dalamud.Game.Config;

namespace XelsCombatAI.Game;

internal sealed class AutoFaceTargetOptionController(Configuration config, DalamudServices services)
{
    private bool overrideActive;
    private uint originalValue;

    public void Update(bool manualMovementActive)
    {
        if (!config.Enabled ||
            !config.ManageMovement ||
            !config.DisableAutoFaceTargetDuringManualMovement ||
            !manualMovementActive)
        {
            this.Restore();
            return;
        }

        this.Disable();
    }

    public void Restore()
    {
        if (!this.overrideActive)
        {
            return;
        }

        if (services.GameConfig.TryGet(UiControlOption.AutoFaceTargetOnAction, out uint currentValue) &&
            currentValue != this.originalValue)
        {
            services.GameConfig.Set(UiControlOption.AutoFaceTargetOnAction, this.originalValue);
        }

        this.overrideActive = false;
        this.originalValue = 0;
    }

    private void Disable()
    {
        if (!services.GameConfig.TryGet(UiControlOption.AutoFaceTargetOnAction, out uint currentValue))
        {
            return;
        }

        if (!this.overrideActive)
        {
            this.originalValue = currentValue;
            this.overrideActive = true;
        }

        if (currentValue != 0)
        {
            services.GameConfig.Set(UiControlOption.AutoFaceTargetOnAction, 0u);
        }
    }
}
