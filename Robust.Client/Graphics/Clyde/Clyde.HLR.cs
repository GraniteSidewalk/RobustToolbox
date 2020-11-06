﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Robust.Client.GameObjects;
using Robust.Client.GameObjects.EntitySystems;
using Robust.Client.Graphics.ClientEye;
using Robust.Client.Graphics.Overlays;
using Robust.Client.Interfaces.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Log;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Utility;
using OpenToolkit.Graphics.OpenGL4;
using System.IO;
using Robust.Shared.Enums;

namespace Robust.Client.Graphics.Clyde
{
    // "HLR" stands for "high level rendering" here.
    // The left side of my monitor only has so much space, OK?
    // The idea is this shouldn't contain too much GL specific stuff.
    internal partial class Clyde
    {
        public ClydeDebugLayers DebugLayers { get; set; }

        private readonly RefList<(SpriteComponent sprite, Matrix3 worldMatrix, Angle worldRotation, float yWorldPos)>
            _drawingSpriteList
                =
                new RefList<(SpriteComponent, Matrix3, Angle, float)>();

        public void Render()
        {
            CheckTransferringScreenshots();

            var size = ScreenSize;
            if (size.X == 0 || size.Y == 0 || _isMinimized)
            {
                ClearFramebuffer(Color.Black);

                // We have to keep running swapbuffers here
                // or else the user's PC will turn into a heater!!
                SwapBuffers();
                return;
            }

            // Completely flush renderer state back to 0.
            // This should make the renderer more robust
            // in case an exception got thrown during rendering of the previous frame.
            ClearRenderState();

            _debugStats.Reset();

            // Basic pre-render busywork.
            // Clear screen to black.
            ClearFramebuffer(Color.Black);

            // Update shared UBOs.
            _updateUniformConstants(_framebufferSize);

            {
                CalcScreenMatrices(ScreenSize, out var proj, out var view);
                SetProjViewFull(proj, view);
            }

            // Short path to render only the splash.
            if (_drawingSplash)
            {
                DrawSplash(_renderHandle);
                FlushRenderQueue();
                SwapBuffers();
                return;
            }

            RenderOverlays(OverlaySpace.ScreenSpaceBelowWorld);

            _mainViewport.Eye = _eyeManager.CurrentEye;
            RenderViewport(_mainViewport);

            {
                var handle = _renderHandle.DrawingHandleScreen;
                var tex = _mainViewport.RenderTarget.Texture;

                handle.DrawTexture(tex, (0, 0));
                FlushRenderQueue();
            }

            TakeScreenshot(ScreenshotType.BeforeUI);

            RenderOverlays(OverlaySpace.ScreenSpace);

            using (DebugGroup("UI"))
            {
                _userInterfaceManager.Render(_renderHandle);
                FlushRenderQueue();
            }

            TakeScreenshot(ScreenshotType.AfterUI);

            // And finally, swap those buffers!
            SwapBuffers();
        }

        private void RenderOverlays(OverlaySpace space)
        {
            using (DebugGroup($"Overlays: {space}"))
            {
                var list = new List<Overlay>();

                foreach (var overlay in _overlayManager.AllOverlays)
                {
                    if ((overlay.Space & space) != 0)
                    {
                        list.Add(overlay);
                    }
                }

                list.Sort(OverlayComparer.Instance);

                for (OverlayPriority i = OverlayPriority.P1; i <= OverlayPriority.P9; i++) {
                    foreach (var overlay in list) {
                        if(i == overlay.Priority)
                            overlay.ClydeRender(_renderHandle, space);
                    }
                }
                FlushRenderQueue();
            }
        }

        private ClydeTexture? ScreenBufferTexture;
        private GLHandle screenBufferHandle;
        private void UpdateOverlayScreenTexture(OverlaySpace space, RenderTexture texture) {
            List<Overlay> oTargets = new List<Overlay>();
            foreach (var overlay in _overlayManager.AllOverlays) {
                if (overlay.RequestScreenTexture && overlay.Space == space) {
                    oTargets.Add(overlay);
                }
            }
            if (oTargets.Count > 0 && ScreenBufferTexture != null) {

                GL.BindTexture(TextureTarget.Texture2D, screenBufferHandle.Handle);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Srgb8Alpha8, _framebufferSize.X, _framebufferSize.Y, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero); //Need to do this every frame in case the screen/framebuffer size changes. Small optimization: just do a vec2 check instead? 
                                                                            //I don't think TexImage2D is very costly but I could be wrong.
                CopyRenderTextureToTexture(texture, ScreenBufferTexture);
                foreach (Overlay overlay in oTargets) {
                    overlay.ScreenTexture = ScreenBufferTexture;
                }
                oTargets.Clear();
            }
        }

        private void DrawEntities(Viewport viewport, Box2 worldBounds)
        {
            if (_eyeManager.CurrentMap == MapId.Nullspace || !_mapManager.HasMapEntity(_eyeManager.CurrentMap))
            {
                return;
            }

            var screenSize = viewport.Size;

            // So we could calculate the correct size of the entities based on the contents of their sprite...
            // Or we can just assume that no entity is larger than 10x10 and get a stupid easy check.
            // TODO: Make this check more accurate.
            var widerBounds = worldBounds.Enlarged(5);

            ProcessSpriteEntities(_eyeManager.CurrentMap, widerBounds, _drawingSpriteList);

            var worldOverlays = new List<Overlay>();

            foreach (var overlay in _overlayManager.AllOverlays)
            {
                if ((overlay.Space & OverlaySpace.WorldSpace) != 0)
                {
                    worldOverlays.Add(overlay);
                }
            }

            worldOverlays.Sort(OverlayComparer.Instance);

            // We use a separate list for indexing so that the sort is faster.
            var indexList = ArrayPool<int>.Shared.Rent(_drawingSpriteList.Count);

            for (var i = 0; i < _drawingSpriteList.Count; i++)
            {
                indexList[i] = i;
            }

            var overlayIndex = 0;
            Array.Sort(indexList, 0, _drawingSpriteList.Count, new SpriteDrawingOrderComparer(_drawingSpriteList));

            for (var i = 0; i < _drawingSpriteList.Count; i++)
            {
                ref var entry = ref _drawingSpriteList[indexList[i]];
                var flushed = false;

                for (var j = overlayIndex; j < worldOverlays.Count; j++)
                {
                    var overlay = worldOverlays[j];

                    if (overlay.ZIndex <= entry.sprite.DrawDepth)
                    {
                        if (!flushed)
                        {
                            FlushRenderQueue();
                            flushed = true;
                        }

                        overlay.ClydeRender(_renderHandle, OverlaySpace.WorldSpace);
                        overlayIndex = j;
                        continue;
                    }

                    break;
                }

                Vector2i roundedPos = default;
                if (entry.sprite.PostShader != null)
                {
                    _renderHandle.UseRenderTarget(EntityPostRenderTarget);
                    _renderHandle.Clear(new Color());
                    // Calculate viewport so that the entity thinks it's drawing to the same position,
                    // which is necessary for light application,
                    // but it's ACTUALLY drawing into the center of the render target.
                    var spritePos = entry.sprite.Owner.Transform.WorldPosition;
                    var screenPos = _eyeManager.WorldToScreen(spritePos);
                    var (roundedX, roundedY) = roundedPos = (Vector2i) screenPos;
                    var flippedPos = new Vector2i(roundedX, screenSize.Y - roundedY);
                    flippedPos -= EntityPostRenderTarget.Size / 2;
                    _renderHandle.Viewport(Box2i.FromDimensions(-flippedPos, screenSize));
                }

                entry.sprite.Render(_renderHandle.DrawingHandleWorld, entry.worldMatrix, entry.worldRotation);

                if (entry.sprite.PostShader != null)
                {
                    var oldProj = _currentMatrixProj;
                    var oldView = _currentMatrixView;

                    _renderHandle.UseRenderTarget(viewport.RenderTarget);
                    _renderHandle.Viewport(Box2i.FromDimensions(Vector2i.Zero, screenSize));

                    _renderHandle.UseShader(entry.sprite.PostShader);
                    CalcScreenMatrices(viewport.Size, out var proj, out var view);
                    _renderHandle.SetProjView(proj, view);
                    _renderHandle.SetModelTransform(Matrix3.Identity);

                    var rounded = roundedPos - EntityPostRenderTarget.Size / 2;

                    var box = Box2i.FromDimensions(rounded, EntityPostRenderTarget.Size);

                    _renderHandle.DrawTextureScreen(EntityPostRenderTarget.Texture,
                        box.BottomLeft, box.BottomRight, box.TopLeft, box.TopRight,
                        Color.White, null);

                    _renderHandle.SetProjView(oldProj, oldView);
                    _renderHandle.UseShader(null);
                }
            }

            _drawingSpriteList.Clear();
            FlushRenderQueue();

            // Cleanup remainders
            foreach (var overlay in worldOverlays)
            {
                overlay.ClydeRender(_renderHandle, OverlaySpace.WorldSpace);
            }

            FlushRenderQueue();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ProcessSpriteEntities(MapId map, Box2 worldBounds,
            RefList<(SpriteComponent sprite, Matrix3 matrix, Angle worldRot, float yWorldPos)> list)
        {
            var spriteSystem = _entitySystemManager.GetEntitySystem<RenderingTreeSystem>();

            var tree = spriteSystem.GetSpriteTreeForMap(map);

            tree.QueryAabb(ref list, ((
                ref RefList<(SpriteComponent sprite, Matrix3 matrix, Angle worldRot, float yWorldPos)> state,
                in SpriteComponent value) =>
            {
                if (value.ContainerOccluded || !value.Visible)
                {
                    return true;
                }

                var entity = value.Owner;
                var transform = entity.Transform;

                ref var entry = ref state.AllocAdd();
                entry.sprite = value;
                entry.worldRot = transform.WorldRotation;
                entry.matrix = transform.WorldMatrix;
                var worldPos = entry.matrix.Transform(transform.LocalPosition);
                entry.yWorldPos = worldPos.Y;
                return true;

            }), worldBounds, approx: true);
        }

        private void DrawSplash(IRenderHandle handle)
        {
            var texture = _resourceCache.GetResource<TextureResource>("/Textures/Logo/logo.png").Texture;

            handle.DrawingHandleScreen.DrawTexture(texture, (ScreenSize - texture.Size) / 2);
        }

        private void RenderViewport(Viewport viewport)
        {
            if (viewport.Eye == null)
            {
                return;
            }

            // TODO: for the love of god all this state pushing/popping needs to be cleaned up.

            var oldTransform = _currentMatrixModel;
            var oldScissor = _currentScissorState;

            // Have to flush the render queue so that all commands finish rendering to the previous framebuffer.
            FlushRenderQueue();

            var eye = viewport.Eye;

            var oldVp = _currentViewport;
            _currentViewport = viewport;

            var state = PushRenderStateFull();

            {
                // Actual code that isn't just pushing/popping renderer state so we can return safely.

                var rt = _currentViewport.RenderTarget;
                BindRenderTargetFull(RtToLoaded(rt));
                ClearFramebuffer(default);
                SetViewportImmediate(Box2i.FromDimensions(Vector2i.Zero, rt.Size));
                _updateUniformConstants(viewport.Size);

                CalcWorldMatrices(rt.Size, eye, out var proj, out var view);
                SetProjViewFull(proj, view);

                // Calculate world-space AABB for camera, to cull off-screen things.
                var worldBounds = Box2.CenteredAround(eye.Position.Position,
                    _framebufferSize / (float) EyeManager.PixelsPerMeter * eye.Zoom);

                if (_eyeManager.CurrentMap != MapId.Nullspace)
                {
                    using (DebugGroup("Lights"))
                    {
                        DrawLightsAndFov(viewport, worldBounds, eye);
                    }

                    using (DebugGroup("Grids"))
                    {
                        _drawGrids(worldBounds);
                    }

                    // We will also render worldspace overlays here so we can do them under / above entities as necessary
                    using (DebugGroup("Entities"))
                    {
                        DrawEntities(viewport, worldBounds);
                    }

                    if (_lightManager.Enabled && _lightManager.DrawHardFov && eye.DrawFov)
                    {

                        ApplyFovToBuffer(viewport, eye);
                    }
                }

                _lightingReady = false;

                if (DebugLayers == ClydeDebugLayers.Fov)
                {
                    // I'm refactoring this code and I found this comment:
                    // NOTE
                    // Yes, it just says "NOTE". Thank you past me.
                    // Anyways I'm 99% sure this was about the fact that this debug layer is actually broken.
                    // Because the math is wrong.
                    // So there are distortions from incorrect projection.
                    _renderHandle.UseShader(_fovDebugShaderInstance);
                    _renderHandle.DrawingHandleScreen.SetTransform(Matrix3.Identity);
                    var pos = UIBox2.FromDimensions(ScreenSize / 2 - (200, 200), (400, 400));
                    _renderHandle.DrawingHandleScreen.DrawTextureRect(FovTexture, pos);
                }

                if (DebugLayers == ClydeDebugLayers.Light)
                {
                    _renderHandle.UseShader(null);
                    _renderHandle.DrawingHandleScreen.SetTransform(Matrix3.Identity);
                    _renderHandle.DrawingHandleScreen.DrawTextureRect(
                        viewport.WallBleedIntermediateRenderTarget2.Texture,
                        UIBox2.FromDimensions(Vector2.Zero, ScreenSize), new Color(1, 1, 1, 0.5f));
                }

                UpdateOverlayScreenTexture(OverlaySpace.WorldSpace, _mainViewport.RenderTarget);
                RenderOverlays(OverlaySpace.WorldSpace);


                UpdateOverlayScreenTexture(OverlaySpace.WorldSpaceFOVStencil, _mainViewport.RenderTarget);
                RenderOverlays(OverlaySpace.WorldSpaceFOVStencil);

            }

            PopRenderStateFull(state);
            _updateUniformConstants(oldVp?.Size ?? _framebufferSize);

            SetScissorFull(oldScissor);
            _currentMatrixModel = oldTransform;
            _currentViewport = oldVp;
        }

        private sealed class OverlayComparer : IComparer<Overlay>
        {
            public static readonly OverlayComparer Instance = new OverlayComparer();

            public int Compare(Overlay? x, Overlay? y)
            {
                var zX = x?.ZIndex ?? 0;
                var zY = y?.ZIndex ?? 0;
                return zX.CompareTo(zY);
            }
        }
    }
}
