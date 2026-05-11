using System;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace XelsCombatAI.Game;

internal sealed unsafe class ManualMovementInputDetector
{
    private const int GamepadAnalogThreshold = 20;

    private static readonly InputId[] MovementInputIds =
    [
        InputId.MOVE_FORE,
        InputId.MOVE_BACK,
        InputId.MOVE_LEFT,
        InputId.MOVE_RIGHT,
        InputId.MOVE_STRIFE_L,
        InputId.MOVE_STRIFE_R,
        InputId.MOVE_AND_STEER,
        InputId.MOVE_DESCENT,
        InputId.MOVE_RETENTION,
        InputId.MOVE_ANGLE_RISING,
        InputId.MOVE_ANGLE_DESCENT,
        InputId.VIRTUAL_PAD_LSTICK_UP,
        InputId.VIRTUAL_PAD_LSTICK_DOWN,
        InputId.VIRTUAL_PAD_LSTICK_LEFT,
        InputId.VIRTUAL_PAD_LSTICK_RIGHT
    ];

    private static readonly InputId[] FacingInputIds =
    [
        InputId.MOVE_LEFT,
        InputId.MOVE_RIGHT,
        InputId.MOVE_AND_STEER,
        InputId.CAMERA_LEFT,
        InputId.CAMERA_RIGHT,
        InputId.CAMERA_MODE,
        InputId.CAMERA_MOUSE_OK,
        InputId.CAMERA_MOUSE_CANCEL,
        InputId.CAMERA_FORWARD,
        InputId.CAMERA_BACKWARD,
        InputId.FACE,
        InputId.VIRTUAL_PAD_RSTICK_LEFT,
        InputId.VIRTUAL_PAD_RSTICK_RIGHT
    ];

    private string status = "unresolved";

    public string Status => this.status;

    public void Reset()
    {
        this.status = "unresolved";
    }

    public bool IsManualMovementRequested()
    {
        try
        {
            var inputData = GetInputData();
            if (inputData == null)
            {
                this.status = "game input unavailable";
                return false;
            }

            foreach (var inputId in MovementInputIds)
            {
                if (inputData->IsInputIdDown(inputId))
                {
                    this.status = "available";
                    return true;
                }
            }

            if (Math.Abs(inputData->GamepadInputs.LeftStickX) >= GamepadAnalogThreshold ||
                Math.Abs(inputData->GamepadInputs.LeftStickY) >= GamepadAnalogThreshold ||
                Math.Abs(inputData->GamepadInputs2.LeftStickX) >= GamepadAnalogThreshold ||
                Math.Abs(inputData->GamepadInputs2.LeftStickY) >= GamepadAnalogThreshold)
            {
                this.status = "available";
                return true;
            }

            this.status = "available";
            return false;
        }
        catch
        {
            this.status = "game input query failed";
            return false;
        }
    }

    public bool IsManualFacingRequested()
    {
        try
        {
            var inputData = GetInputData();
            if (inputData == null)
            {
                this.status = "game input unavailable";
                return false;
            }

            foreach (var inputId in FacingInputIds)
            {
                if (inputData->IsInputIdDown(inputId))
                {
                    this.status = "available";
                    return true;
                }
            }

            if (Math.Abs(inputData->GamepadInputs.RightStickX) >= GamepadAnalogThreshold ||
                Math.Abs(inputData->GamepadInputs.RightStickY) >= GamepadAnalogThreshold ||
                Math.Abs(inputData->GamepadInputs2.RightStickX) >= GamepadAnalogThreshold ||
                Math.Abs(inputData->GamepadInputs2.RightStickY) >= GamepadAnalogThreshold)
            {
                this.status = "available";
                return true;
            }

            var mouseButtons = inputData->CursorInputs.MouseButtonHeldFlags;
            if (mouseButtons.HasFlag(MouseButtonFlags.LBUTTON | MouseButtonFlags.RBUTTON) ||
                mouseButtons.HasFlag(MouseButtonFlags.RBUTTON))
            {
                if (inputData->CursorInputs.DeltaX != 0 || inputData->CursorInputs.DeltaY != 0)
                {
                    this.status = "available";
                    return true;
                }
            }

            this.status = "available";
            return false;
        }
        catch
        {
            this.status = "game input query failed";
            return false;
        }
    }

    private static InputData* GetInputData()
    {
        var framework = Framework.Instance();
        if (framework == null)
        {
            return null;
        }

        var uiModule = framework->GetUIModule();
        return uiModule == null ? null : (InputData*)uiModule->GetUIInputData();
    }
}
