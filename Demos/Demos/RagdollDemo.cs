﻿using BepuUtilities;
using DemoRenderer;
using BepuPhysics;
using BepuPhysics.Collidables;
using System.Numerics;
using Quaternion = BepuUtilities.Quaternion;
using System;
using BepuPhysics.CollisionDetection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using BepuPhysics.Constraints;
using DemoContentLoader;
using DemoUtilities;
using BepuUtilities.Memory;

namespace Demos.Demos
{
    /// <summary>
    /// Bit masks which control whether different members of a group of objects can collide with each other.
    /// </summary>
    public struct SubgroupCollisionFilter
    {
        /// <summary>
        /// A mask of 16 bits, each set bit representing a collision group that an object belongs to.
        /// </summary>
        public ushort SubgroupMembership;
        /// <summary>
        /// A mask of 16 bits, each set bit representing a collision group that an object can interact with.
        /// </summary>
        public ushort CollidableSubgroups;
        /// <summary>
        /// Id of the owner of the object. Objects belonging to different groups always collide.
        /// </summary>
        public int GroupId;

        /// <summary>
        /// Initializes a collision filter that collides with everything in the group.
        /// </summary>
        /// <param name="groupId">Id of the group that this filter operates within.</param>
        public SubgroupCollisionFilter(int groupId)
        {
            GroupId = groupId;
            SubgroupMembership = ushort.MaxValue;
            CollidableSubgroups = ushort.MaxValue;
        }

        /// <summary>
        /// Initializes a collision filter that belongs to one specific subgroup and can collide with any other subgroup.
        /// </summary>
        /// <param name="groupId">Id of the group that this filter operates within.</param>
        /// <param name="subgroupId">Id of the subgroup to put this ragdoll</param>
        public SubgroupCollisionFilter(int groupId, int subgroupId)
        {
            GroupId = groupId;
            Debug.Assert(subgroupId >= 0 && subgroupId < 16, "The subgroup field is a ushort; it can only hold 16 distinct subgroups.");
            SubgroupMembership = (ushort)(1 << subgroupId);
            CollidableSubgroups = ushort.MaxValue;
        }

        /// <summary>
        /// Disables a collision between this filter and the specified subgroup.
        /// </summary>
        /// <param name="subgroupId">Subgroup id to disable collision with.</param>
        public void DisableCollision(int subgroupId)
        {
            Debug.Assert(subgroupId >= 0 && subgroupId < 16, "The subgroup field is a ushort; it can only hold 16 distinct subgroups.");
            CollidableSubgroups ^= (ushort)(1 << subgroupId);
        }

        /// <summary>
        /// Modifies the interactable subgroups such that filterB does not interact with the subgroups defined by filter a and vice versa.
        /// </summary>
        /// <param name="a">Filter from which to remove collisions with filter b's subgroups.</param>
        /// <param name="b">Filter from which to remove collisions with filter a's subgroups.</param>
        public static void DisableCollision(ref SubgroupCollisionFilter filterA, ref SubgroupCollisionFilter filterB)
        {
            filterA.CollidableSubgroups ^= filterB.SubgroupMembership;
            filterB.CollidableSubgroups ^= filterA.SubgroupMembership;
        }

    }

    /// <summary>
    /// Narrow phase callbacks that prune out collisions between members of a group of objects.
    /// </summary>
    struct SubgroupFilteredCallbacks : INarrowPhaseCallbacks
    {
        public BodyProperty<SubgroupCollisionFilter> CollisionFilters;
        public void Initialize(Simulation simulation)
        {
            CollisionFilters.Initialize(simulation.Bodies);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b)
        {
            if (a.Mobility == CollidableMobility.Dynamic && b.Mobility == CollidableMobility.Dynamic)
            {
                ref var maskA = ref CollisionFilters[a.Handle];
                ref var maskB = ref CollisionFilters[b.Handle];
                //Different instances are always allowed to collide.
                //Note that this only tests a's accepted groups against b's membership, instead of both directions.
                return maskA.GroupId != maskB.GroupId || (maskA.CollidableSubgroups & maskB.SubgroupMembership) > 0;
                //This demo will ensure symmetry for simplicity. Optionally, you could make use of the fact that collidable references obey an order;
                //the lower valued handle will always be CollidableReference a. Static collidables will always be in CollidableReference b if they exist.
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        {
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ConfigureMaterial(out PairMaterialProperties pairMaterial)
        {
            pairMaterial.FrictionCoefficient = 1;
            pairMaterial.MaximumRecoveryVelocity = 2f;
            pairMaterial.SpringSettings = new SpringSettings(30, 1);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool ConfigureContactManifold(int workerIndex, CollidablePair pair, NonconvexContactManifold* manifold, out PairMaterialProperties pairMaterial)
        {
            ConfigureMaterial(out pairMaterial);
            return true;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool ConfigureContactManifold(int workerIndex, CollidablePair pair, ConvexContactManifold* manifold, out PairMaterialProperties pairMaterial)
        {
            ConfigureMaterial(out pairMaterial);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ConvexContactManifold* manifold)
        {
            return true;
        }

        public void Dispose()
        {
            CollisionFilters.Dispose();
        }
    }

    public class RagdollDemo : Demo
    {
        static BodyReference AddBody<TShape>(TShape shape, float mass, in RigidPose pose, Simulation simulation) where TShape : struct, IConvexShape
        {
            //Note that this always registers a new shape instance. You could be more clever/efficient and share shapes, but the goal here is to show the most basic option.
            //Also, the cost of registering different shapes isn't that high for tiny implicit shapes.
            var shapeIndex = simulation.Shapes.Add(shape);
            shape.ComputeInertia(mass, out var inertia);
            var description = BodyDescription.CreateDynamic(pose, inertia, new CollidableDescription(shapeIndex, 0.1f), new BodyActivityDescription(0.01f));
            return new BodyReference(simulation.Bodies.Add(description), simulation.Bodies);
        }

        static RigidPose GetWorldPose(Vector3 localPosition, Quaternion localOrientation, RigidPose ragdollPose)
        {
            RigidPose worldPose;
            RigidPose.Transform(localPosition, ragdollPose, out worldPose.Position);
            Quaternion.ConcatenateWithoutOverlap(localOrientation, ragdollPose.Orientation, out worldPose.Orientation);
            return worldPose;
        }
        static void GetCapsuleForLineSegment(Vector3 start, Vector3 end, float radius, out Capsule capsule, out Vector3 position, out Quaternion orientation)
        {
            position = 0.5f * (start + end);

            var offset = end - start;
            capsule.HalfLength = 0.5f * offset.Length();
            capsule.Radius = radius;
            //The capsule shape's length is along its local Y axis, so get the shortest rotation from Y to the current orientation.
            var cross = Vector3.Cross(offset / capsule.Length, new Vector3(0, 1, 0));
            var crossLength = cross.Length();
            orientation = crossLength > 1e-8f ? Quaternion.CreateFromAxisAngle(cross / crossLength, (float)Math.Asin(crossLength)) : Quaternion.Identity;
        }

        public static Quaternion CreateBasis(in Vector3 z, in Vector3 x)
        {
            //For ease of use, don't assume that x is perpendicular to z, nor that either input is normalized.
            Matrix3x3 basis;
            basis.Z = Vector3.Normalize(z);
            Vector3x.Cross(basis.Z, x, out basis.Y);
            basis.Y = Vector3.Normalize(basis.Y);
            Vector3x.Cross(basis.Y, basis.Z, out basis.X);
            Quaternion.CreateFromRotationMatrix(basis, out var toReturn);
            return toReturn;
        }

        static AngularMotor BuildAngularMotor()
        {
            //By default, these motors use nonzero softness (inverse damping) to damp the relative motion between ragdoll pieces.
            //If you set the damping to 0 and then set the maximum force to some finite value (75 works reasonably well), the ragdolls act more like action figures.
            //You could also replace the AngularMotors with AngularServos and provide actual relative orientation goals for physics-driven animation.
            return new AngularMotor { TargetVelocityLocalA = new Vector3(), Settings = new MotorSettings(float.MaxValue, 0.01f) };
        }

        static void AddArm(float sign, Vector3 localShoulder, RigidPose localChestPose, int chestHandle, ref SubgroupCollisionFilter chestMask,
            int limbBaseBitIndex, int ragdollIndex, RigidPose ragdollPose, BodyProperty<SubgroupCollisionFilter> filters, SpringSettings constraintSpringSettings, Simulation simulation)
        {
            var localElbow = localShoulder + new Vector3(sign * 0.45f, 0, 0);
            var localWrist = localElbow + new Vector3(sign * 0.45f, 0, 0);
            var handPosition = localWrist + new Vector3(sign * 0.1f, 0, 0);
            GetCapsuleForLineSegment(localShoulder, localElbow, 0.1f, out var upperArmShape, out var upperArmPosition, out var upperArmOrientation);
            var upperArm = AddBody(upperArmShape, 5, GetWorldPose(upperArmPosition, upperArmOrientation, ragdollPose), simulation);
            GetCapsuleForLineSegment(localElbow, localWrist, 0.09f, out var lowerArmShape, out var lowerArmPosition, out var lowerArmOrientation);
            var lowerArm = AddBody(lowerArmShape, 5, GetWorldPose(lowerArmPosition, lowerArmOrientation, ragdollPose), simulation);
            var hand = AddBody(new Box(0.2f, 0.1f, 0.2f), 2, GetWorldPose(handPosition, Quaternion.Identity, ragdollPose), simulation);

            //Create joints between limb pieces.
            //Chest-Upper Arm
            simulation.Solver.Add(chestHandle, upperArm.Handle, new BallSocket
            {
                LocalOffsetA = Quaternion.Transform(localShoulder - localChestPose.Position, Quaternion.Conjugate(localChestPose.Orientation)),
                LocalOffsetB = Quaternion.Transform(localShoulder - upperArmPosition, Quaternion.Conjugate(upperArmOrientation)),
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(chestHandle, upperArm.Handle, new SwingLimit
            {
                AxisLocalA = Quaternion.Transform(Vector3.Normalize(new Vector3(sign, 0, 1)), Quaternion.Conjugate(localChestPose.Orientation)),
                AxisLocalB = Quaternion.Transform(new Vector3(sign, 0, 0), Quaternion.Conjugate(upperArmOrientation)),
                MaximumSwingAngle = MathHelper.Pi * 0.56f,
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(chestHandle, upperArm.Handle, new TwistLimit
            {
                LocalBasisA = Quaternion.Concatenate(CreateBasis(new Vector3(1, 0, 0), new Vector3(0, 0, -1)), Quaternion.Conjugate(localChestPose.Orientation)),
                LocalBasisB = Quaternion.Concatenate(CreateBasis(new Vector3(1, 0, 0), new Vector3(0, 0, -1)), Quaternion.Conjugate(upperArmOrientation)),
                MinimumAngle = MathHelper.Pi * -0.55f,
                MaximumAngle = MathHelper.Pi * 0.55f,
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(chestHandle, upperArm.Handle, BuildAngularMotor());

            //Upper Arm-Lower Arm
            simulation.Solver.Add(upperArm.Handle, lowerArm.Handle, new SwivelHinge
            {
                LocalOffsetA = Quaternion.Transform(localElbow - upperArmPosition, Quaternion.Conjugate(upperArmOrientation)),
                LocalSwivelAxisA = new Vector3(1, 0, 0),
                LocalOffsetB = Quaternion.Transform(localElbow - lowerArmPosition, Quaternion.Conjugate(lowerArmOrientation)),
                LocalHingeAxisB = new Vector3(0, 1, 0),
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(upperArm.Handle, lowerArm.Handle, new SwingLimit
            {
                AxisLocalA = new Vector3(0, 1, 0),
                AxisLocalB = new Vector3(sign, 0, 0),
                MaximumSwingAngle = MathHelper.PiOver2,
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(upperArm.Handle, lowerArm.Handle, new TwistLimit
            {
                LocalBasisA = Quaternion.Concatenate(CreateBasis(new Vector3(1, 0, 0), new Vector3(0, 0, -1)), Quaternion.Conjugate(upperArmOrientation)),
                LocalBasisB = Quaternion.Concatenate(CreateBasis(new Vector3(1, 0, 0), new Vector3(0, 0, -1)), Quaternion.Conjugate(lowerArmOrientation)),
                MinimumAngle = MathHelper.Pi * -0.55f,
                MaximumAngle = MathHelper.Pi * 0.55f,
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(upperArm.Handle, lowerArm.Handle, BuildAngularMotor());

            //Lower Arm-Hand
            simulation.Solver.Add(lowerArm.Handle, hand.Handle, new BallSocket
            {
                LocalOffsetA = Quaternion.Transform(localWrist - lowerArmPosition, Quaternion.Conjugate(lowerArmOrientation)),
                LocalOffsetB = localWrist - handPosition,
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(lowerArm.Handle, hand.Handle, new SwingLimit
            {
                AxisLocalA = Quaternion.Transform(new Vector3(sign, 0, 0), Quaternion.Conjugate(lowerArmOrientation)),
                AxisLocalB = new Vector3(sign, 0, 0),
                MaximumSwingAngle = MathHelper.PiOver2,
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(lowerArm.Handle, hand.Handle, new TwistServo
            {
                LocalBasisA = Quaternion.Concatenate(CreateBasis(new Vector3(1, 0, 0), new Vector3(0, 0, 1)), Quaternion.Conjugate(lowerArmOrientation)),
                LocalBasisB = CreateBasis(new Vector3(1, 0, 0), new Vector3(0, 0, 1)),
                TargetAngle = 0,
                SpringSettings = constraintSpringSettings,
                ServoSettings = new ServoSettings(float.MaxValue, 0, float.MaxValue)
            });
            simulation.Solver.Add(lowerArm.Handle, hand.Handle, BuildAngularMotor());

            //Disable collisions between connected ragdoll pieces.
            var upperArmLocalIndex = limbBaseBitIndex;
            var lowerArmLocalIndex = limbBaseBitIndex + 1;
            var handLocalIndex = limbBaseBitIndex + 2;
            ref var upperArmFilter = ref filters.Allocate(upperArm.Handle);
            ref var lowerArmFilter = ref filters.Allocate(lowerArm.Handle);
            ref var handFilter = ref filters.Allocate(hand.Handle);
            upperArmFilter = new SubgroupCollisionFilter(ragdollIndex, upperArmLocalIndex);
            lowerArmFilter = new SubgroupCollisionFilter(ragdollIndex, lowerArmLocalIndex);
            handFilter = new SubgroupCollisionFilter(ragdollIndex, handLocalIndex);
            SubgroupCollisionFilter.DisableCollision(ref chestMask, ref upperArmFilter);
            SubgroupCollisionFilter.DisableCollision(ref upperArmFilter, ref lowerArmFilter);
            SubgroupCollisionFilter.DisableCollision(ref lowerArmFilter, ref handFilter);

        }

        static void AddLeg(Vector3 localHip, RigidPose localHipsPose, int hipsHandle, ref SubgroupCollisionFilter hipsFilter,
            int limbBaseBitIndex, int ragdollIndex, RigidPose ragdollPose, BodyProperty<SubgroupCollisionFilter> filters, SpringSettings constraintSpringSettings, Simulation simulation)
        {
            var localKnee = localHip - new Vector3(0, 0.5f, 0);
            var localAnkle = localKnee - new Vector3(0, 0.5f, 0);
            var localFoot = localAnkle + new Vector3(0, -0.075f, 0.05f);
            GetCapsuleForLineSegment(localHip, localKnee, 0.12f, out var upperLegShape, out var upperLegPosition, out var upperLegOrientation);
            var upperLeg = AddBody(upperLegShape, 5, GetWorldPose(upperLegPosition, upperLegOrientation, ragdollPose), simulation);
            GetCapsuleForLineSegment(localKnee, localAnkle, 0.11f, out var lowerLegShape, out var lowerLegPosition, out var lowerLegOrientation);
            var lowerLeg = AddBody(lowerLegShape, 5, GetWorldPose(lowerLegPosition, lowerLegOrientation, ragdollPose), simulation);
            var foot = AddBody(new Box(0.2f, 0.15f, 0.3f), 2, GetWorldPose(localFoot, Quaternion.Identity, ragdollPose), simulation);

            //Create joints between limb pieces.
            //Hips-Upper Leg
            simulation.Solver.Add(hipsHandle, upperLeg.Handle, new BallSocket
            {
                LocalOffsetA = Quaternion.Transform(localHip - localHipsPose.Position, Quaternion.Conjugate(localHipsPose.Orientation)),
                LocalOffsetB = Quaternion.Transform(localHip - upperLegPosition, Quaternion.Conjugate(upperLegOrientation)),
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(hipsHandle, upperLeg.Handle, new SwingLimit
            {
                AxisLocalA = Quaternion.Transform(Vector3.Normalize(new Vector3(Math.Sign(localHip.X), -1, 0)), Quaternion.Conjugate(localHipsPose.Orientation)),
                AxisLocalB = Quaternion.Transform(new Vector3(0, -1, 0), Quaternion.Conjugate(upperLegOrientation)),
                MaximumSwingAngle = MathHelper.PiOver2,
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(hipsHandle, upperLeg.Handle, new TwistLimit
            {
                LocalBasisA = Quaternion.Concatenate(CreateBasis(new Vector3(0, -1, 0), new Vector3(0, 0, 1)), Quaternion.Conjugate(localHipsPose.Orientation)),
                LocalBasisB = Quaternion.Concatenate(CreateBasis(new Vector3(0, -1, 0), new Vector3(0, 0, 1)), Quaternion.Conjugate(upperLegOrientation)),
                MinimumAngle = localHip.X < 0 ? MathHelper.Pi * -0.05f : MathHelper.Pi * -0.55f,
                MaximumAngle = localHip.X < 0 ? MathHelper.Pi * 0.55f : MathHelper.Pi * 0.05f,
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(hipsHandle, upperLeg.Handle, BuildAngularMotor());

            //Upper Leg-Lower Leg
            simulation.Solver.Add(upperLeg.Handle, lowerLeg.Handle, new Hinge
            {
                LocalHingeAxisA = Quaternion.Transform(new Vector3(1, 0, 0), Quaternion.Conjugate(upperLegOrientation)),
                LocalOffsetA = Quaternion.Transform(localKnee - upperLegPosition, Quaternion.Conjugate(upperLegOrientation)),
                LocalHingeAxisB = Quaternion.Transform(new Vector3(1, 0, 0), Quaternion.Conjugate(lowerLegOrientation)),
                LocalOffsetB = Quaternion.Transform(localKnee - lowerLegPosition, Quaternion.Conjugate(lowerLegOrientation)),
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(upperLeg.Handle, lowerLeg.Handle, new SwingLimit
            {
                AxisLocalA = Quaternion.Transform(new Vector3(0, 0, 1), Quaternion.Conjugate(upperLegOrientation)),
                AxisLocalB = Quaternion.Transform(new Vector3(0, 1, 0), Quaternion.Conjugate(lowerLegOrientation)),
                MaximumSwingAngle = MathHelper.PiOver2,
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(upperLeg.Handle, lowerLeg.Handle, BuildAngularMotor());

            //Lower Leg-Foot
            simulation.Solver.Add(lowerLeg.Handle, foot.Handle, new BallSocket
            {
                LocalOffsetA = Quaternion.Transform(localAnkle - lowerLegPosition, Quaternion.Conjugate(lowerLegOrientation)),
                LocalOffsetB = localAnkle - localFoot,
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(lowerLeg.Handle, foot.Handle, new SwingLimit
            {
                AxisLocalA = Quaternion.Transform(new Vector3(0, 1, 0), Quaternion.Conjugate(lowerLegOrientation)),
                AxisLocalB = new Vector3(0, 1, 0),
                MaximumSwingAngle = 1,
                SpringSettings = constraintSpringSettings
            });
            simulation.Solver.Add(lowerLeg.Handle, foot.Handle, new TwistServo
            {
                LocalBasisA = Quaternion.Concatenate(CreateBasis(new Vector3(0, 1, 0), new Vector3(0, 0, 1)), Quaternion.Conjugate(lowerLegOrientation)),
                LocalBasisB = CreateBasis(new Vector3(0, 1, 0), new Vector3(0, 0, 1)),
                TargetAngle = 0,
                SpringSettings = constraintSpringSettings,
                ServoSettings = new ServoSettings(float.MaxValue, 0, float.MaxValue)
            });
            simulation.Solver.Add(lowerLeg.Handle, foot.Handle, BuildAngularMotor());

            //Disable collisions between connected ragdoll pieces.
            var upperLegLocalIndex = limbBaseBitIndex;
            var lowerLegLocalIndex = limbBaseBitIndex + 1;
            var footLocalIndex = limbBaseBitIndex + 2;
            ref var upperLegFilter = ref filters.Allocate(upperLeg.Handle);
            ref var lowerLegFilter = ref filters.Allocate(lowerLeg.Handle);
            ref var footFilter = ref filters.Allocate(foot.Handle);
            upperLegFilter = new SubgroupCollisionFilter(ragdollIndex, upperLegLocalIndex);
            lowerLegFilter = new SubgroupCollisionFilter(ragdollIndex, lowerLegLocalIndex);
            footFilter = new SubgroupCollisionFilter(ragdollIndex, footLocalIndex);
            SubgroupCollisionFilter.DisableCollision(ref hipsFilter, ref upperLegFilter);
            SubgroupCollisionFilter.DisableCollision(ref upperLegFilter, ref lowerLegFilter);
            SubgroupCollisionFilter.DisableCollision(ref lowerLegFilter, ref footFilter);
        }

        public static void AddRagdoll(Vector3 position, Quaternion orientation, int ragdollIndex, BodyProperty<SubgroupCollisionFilter> collisionFilters, Simulation simulation)
        {
            var ragdollPose = new RigidPose { Position = position, Orientation = orientation };
            var horizontalOrientation = Quaternion.CreateFromAxisAngle(new Vector3(0, 0, 1), MathHelper.PiOver2);
            var hipsPose = new RigidPose { Position = new Vector3(0, 1.1f, 0), Orientation = horizontalOrientation };
            var hips = AddBody(new Capsule(0.17f, 0.25f), 8, GetWorldPose(hipsPose.Position, hipsPose.Orientation, ragdollPose), simulation);
            var abdomenPose = new RigidPose { Position = new Vector3(0, 1.3f, 0), Orientation = horizontalOrientation };
            var abdomen = AddBody(new Capsule(0.17f, 0.22f), 7, GetWorldPose(abdomenPose.Position, abdomenPose.Orientation, ragdollPose), simulation);
            var chestPose = new RigidPose { Position = new Vector3(0, 1.6f, 0), Orientation = horizontalOrientation };
            var chest = AddBody(new Capsule(0.21f, 0.3f), 10, GetWorldPose(chestPose.Position, chestPose.Orientation, ragdollPose), simulation);
            var headPose = new RigidPose { Position = new Vector3(0, 2.05f, 0), Orientation = Quaternion.Identity };
            var head = AddBody(new Sphere(0.2f), 5, GetWorldPose(headPose.Position, headPose.Orientation, ragdollPose), simulation);

            //Attach constraints between torso pieces.
            var springSettings = new SpringSettings(15f, 1f);
            var lowerSpine = (hipsPose.Position + abdomenPose.Position) * 0.5f;
            //Hips-Abdomen
            simulation.Solver.Add(hips.Handle, abdomen.Handle, new BallSocket
            {
                LocalOffsetA = Quaternion.Transform(lowerSpine - hipsPose.Position, Quaternion.Conjugate(hipsPose.Orientation)),
                LocalOffsetB = Quaternion.Transform(lowerSpine - abdomenPose.Position, Quaternion.Conjugate(abdomenPose.Orientation)),
                SpringSettings = springSettings
            });
            simulation.Solver.Add(hips.Handle, abdomen.Handle, new SwingLimit
            {
                AxisLocalA = Quaternion.Transform(new Vector3(0, 1, 0), Quaternion.Conjugate(hipsPose.Orientation)),
                AxisLocalB = Quaternion.Transform(new Vector3(0, 1, 0), Quaternion.Conjugate(abdomenPose.Orientation)),
                MaximumSwingAngle = MathHelper.Pi * 0.27f,
                SpringSettings = springSettings
            });
            simulation.Solver.Add(hips.Handle, abdomen.Handle, new TwistLimit
            {
                LocalBasisA = Quaternion.Concatenate(CreateBasis(new Vector3(0, 1, 0), new Vector3(1, 0, 0)), Quaternion.Conjugate(hipsPose.Orientation)),
                LocalBasisB = Quaternion.Concatenate(CreateBasis(new Vector3(0, 1, 0), new Vector3(1, 0, 0)), Quaternion.Conjugate(abdomenPose.Orientation)),
                MinimumAngle = MathHelper.Pi * -0.2f,
                MaximumAngle = MathHelper.Pi * 0.2f,
                SpringSettings = springSettings
            });
            simulation.Solver.Add(hips.Handle, abdomen.Handle, BuildAngularMotor());
            //Abdomen-Chest
            var upperSpine = (abdomenPose.Position + chestPose.Position) * 0.5f;
            simulation.Solver.Add(abdomen.Handle, chest.Handle, new BallSocket
            {
                LocalOffsetA = Quaternion.Transform(upperSpine - abdomenPose.Position, Quaternion.Conjugate(abdomenPose.Orientation)),
                LocalOffsetB = Quaternion.Transform(upperSpine - chestPose.Position, Quaternion.Conjugate(chestPose.Orientation)),
                SpringSettings = springSettings
            });
            simulation.Solver.Add(abdomen.Handle, chest.Handle, new SwingLimit
            {
                AxisLocalA = Quaternion.Transform(new Vector3(0, 1, 0), Quaternion.Conjugate(abdomenPose.Orientation)),
                AxisLocalB = Quaternion.Transform(new Vector3(0, 1, 0), Quaternion.Conjugate(chestPose.Orientation)),
                MaximumSwingAngle = MathHelper.Pi * 0.27f,
                SpringSettings = springSettings
            });
            simulation.Solver.Add(abdomen.Handle, chest.Handle, new TwistLimit
            {
                LocalBasisA = Quaternion.Concatenate(CreateBasis(new Vector3(0, 1, 0), new Vector3(1, 0, 0)), Quaternion.Conjugate(abdomenPose.Orientation)),
                LocalBasisB = Quaternion.Concatenate(CreateBasis(new Vector3(0, 1, 0), new Vector3(1, 0, 0)), Quaternion.Conjugate(chestPose.Orientation)),
                MinimumAngle = MathHelper.Pi * -0.2f,
                MaximumAngle = MathHelper.Pi * 0.2f,
                SpringSettings = springSettings
            });
            simulation.Solver.Add(abdomen.Handle, chest.Handle, BuildAngularMotor());
            //Chest-Head
            var neck = (headPose.Position + chestPose.Position) * 0.5f;
            simulation.Solver.Add(chest.Handle, head.Handle, new BallSocket
            {
                LocalOffsetA = Quaternion.Transform(neck - chestPose.Position, Quaternion.Conjugate(chestPose.Orientation)),
                LocalOffsetB = neck - headPose.Position,
                SpringSettings = springSettings
            });
            simulation.Solver.Add(chest.Handle, head.Handle, new SwingLimit
            {
                AxisLocalA = Quaternion.Transform(new Vector3(0, 1, 0), Quaternion.Conjugate(chestPose.Orientation)),
                AxisLocalB = new Vector3(0, 1, 0),
                MaximumSwingAngle = MathHelper.PiOver2 * 0.9f,
                SpringSettings = springSettings
            });
            simulation.Solver.Add(chest.Handle, head.Handle, new TwistLimit
            {
                LocalBasisA = Quaternion.Concatenate(CreateBasis(new Vector3(0, 1, 0), new Vector3(1, 0, 0)), Quaternion.Conjugate(chestPose.Orientation)),
                LocalBasisB = Quaternion.Concatenate(CreateBasis(new Vector3(0, 1, 0), new Vector3(1, 0, 0)), Quaternion.Conjugate(headPose.Orientation)),
                MinimumAngle = MathHelper.Pi * -0.5f,
                MaximumAngle = MathHelper.Pi * 0.5f,
                SpringSettings = springSettings
            });
            simulation.Solver.Add(chest.Handle, head.Handle, BuildAngularMotor());

            var hipsLocalIndex = 0;
            var abdomenLocalIndex = 1;
            var chestLocalIndex = 2;
            var headLocalIndex = 3;
            ref var hipsFilter = ref collisionFilters.Allocate(hips.Handle);
            ref var abdomenFilter = ref collisionFilters.Allocate(abdomen.Handle);
            ref var chestFilter = ref collisionFilters.Allocate(chest.Handle);
            ref var headFilter = ref collisionFilters.Allocate(head.Handle);
            hipsFilter = new SubgroupCollisionFilter(ragdollIndex, hipsLocalIndex);
            abdomenFilter = new SubgroupCollisionFilter(ragdollIndex, abdomenLocalIndex);
            chestFilter = new SubgroupCollisionFilter(ragdollIndex, chestLocalIndex);
            headFilter = new SubgroupCollisionFilter(ragdollIndex, headLocalIndex);
            //Disable collisions in the torso and head.
            SubgroupCollisionFilter.DisableCollision(ref hipsFilter, ref abdomenFilter);
            SubgroupCollisionFilter.DisableCollision(ref abdomenFilter, ref chestFilter);
            SubgroupCollisionFilter.DisableCollision(ref chestFilter, ref headFilter);

            //Build all the limbs. Setting the masks is delayed until after the limbs have been created and have disabled collisions with the chest/hips.
            AddArm(1, chestPose.Position + new Vector3(0.4f, 0.1f, 0), chestPose, chest.Handle, ref chestFilter, 4, ragdollIndex, ragdollPose, collisionFilters, springSettings, simulation);
            AddArm(-1, chestPose.Position + new Vector3(-0.4f, 0.1f, 0), chestPose, chest.Handle, ref chestFilter, 7, ragdollIndex, ragdollPose, collisionFilters, springSettings, simulation);
            AddLeg(hipsPose.Position + new Vector3(-0.17f, -0.2f, 0), hipsPose, hips.Handle, ref hipsFilter, 10, ragdollIndex, ragdollPose, collisionFilters, springSettings, simulation);
            AddLeg(hipsPose.Position + new Vector3(0.17f, -0.2f, 0), hipsPose, hips.Handle, ref hipsFilter, 13, ragdollIndex, ragdollPose, collisionFilters, springSettings, simulation);
        }

        public unsafe override void Initialize(ContentArchive content, Camera camera)
        {
            camera.Position = new Vector3(-20, 10, -20);
            camera.Yaw = MathHelper.Pi * 3f / 4;
            camera.Pitch = MathHelper.Pi * 0.05f;
            var collisionFilters = new BodyProperty<SubgroupCollisionFilter>();
            Simulation = Simulation.Create(BufferPool, new SubgroupFilteredCallbacks { CollisionFilters = collisionFilters }, new DemoPoseIntegratorCallbacks(new Vector3(0, -10, 0)));

            int ragdollIndex = 0;
            var spacing = new Vector3(2f, 3, 1);
            int width = 8;
            int height = 8;
            int length = 8;
            var origin = -0.5f * spacing * new Vector3(width, 0, length) + new Vector3(0, 0.2f, 0);
            for (int i = 0; i < width; ++i)
            {
                for (int j = 0; j < height; ++j)
                {
                    for (int k = 0; k < length; ++k)
                    {
                        AddRagdoll(origin + spacing * new Vector3(i, j, k), Quaternion.CreateFromAxisAngle(new Vector3(0, 1, 0), MathHelper.Pi * 0.05f), ragdollIndex++, collisionFilters, Simulation);
                    }
                }
            }

            Simulation.Statics.Add(new StaticDescription(new Vector3(0, -0.5f, 0), new CollidableDescription(Simulation.Shapes.Add(new Box(300, 1, 300)), 0.1f)));
        }

    }
}


