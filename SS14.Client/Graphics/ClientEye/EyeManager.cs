﻿using System;
using SS14.Client.Interfaces;
using SS14.Client.Interfaces.Graphics.ClientEye;
using SS14.Client.Utility;
using SS14.Shared.Interfaces.Map;
using SS14.Shared.IoC;
using SS14.Shared.Map;
using SS14.Shared.Maths;

namespace SS14.Client.Graphics.ClientEye
{
    public sealed class EyeManager : IEyeManager, IDisposable
    {
        // If you modify this make sure to edit the value in the SS14.Shared.Audio.AudioParams struct default too!
        // No I can't be bothered to make this a shared constant.
        public const int PIXELSPERMETER = 32;

        [Dependency]
        readonly ISceneTreeHolder sceneTree;

        // We default to this when we get set to a null eye.
        private FixedEye defaultEye;

        private IEye currentEye;
        public IEye CurrentEye
        {
            get => currentEye;
            set
            {
                if (currentEye == value)
                {
                    return;
                }

                currentEye.GodotCamera.Current = false;
                if (value != null)
                {
                    currentEye = value;
                }
                else
                {
                    currentEye = defaultEye;
                }

                currentEye.GodotCamera.Current = true;
            }
        }

        public MapId CurrentMap => currentEye.MapId;

        public Box2 GetWorldViewport()
        {
            var vpSize = sceneTree.SceneTree.Root.Size.Convert();

            var topLeft = ScreenToWorld(Vector2.Zero);
            var topRight = ScreenToWorld(new Vector2(vpSize.X, 0));
            var bottomRight = ScreenToWorld(vpSize);
            var bottomLeft = ScreenToWorld(new Vector2(0, vpSize.Y));

            var left = MathHelper.Min(topLeft.X, topRight.X, bottomRight.X, bottomLeft.X);
            var top = MathHelper.Min(topLeft.Y, topRight.Y, bottomRight.Y, bottomLeft.Y);
            var right = MathHelper.Max(topLeft.X, topRight.X, bottomRight.X, bottomLeft.X);
            var bottom = MathHelper.Max(topLeft.Y, topRight.Y, bottomRight.Y, bottomLeft.Y);

            return new Box2(left, top, right, bottom);
        }

        public void Initialize()
        {
            defaultEye = new FixedEye();
            currentEye = defaultEye;
            currentEye.GodotCamera.Current = true;
        }

        public void Dispose()
        {
            defaultEye.Dispose();
        }

        public Vector2 WorldToScreen(Vector2 point)
        {
            var transform = sceneTree.WorldRoot.GetViewportTransform();
            return transform.Xform(point.Convert() * PIXELSPERMETER).Convert();
        }

        public ScreenCoordinates WorldToScreen(LocalCoordinates point)
        {
            var pos = WorldToScreen(point.Position);
            return new ScreenCoordinates(pos, point.MapID);
        }

        public LocalCoordinates ScreenToWorld(ScreenCoordinates point)
        {
            var pos = ScreenToWorld(point.Position);
            var grid = IoCManager.Resolve<IMapManager>().GetMap(point.MapID).FindGridAt(pos);
            return new LocalCoordinates(pos, grid);
        }

        public Vector2 ScreenToWorld(Vector2 point)
        {
            var transform = sceneTree.WorldRoot.GetViewportTransform();
            return transform.XformInv(point.Convert()).Convert() / PIXELSPERMETER;
        }
    }
}
