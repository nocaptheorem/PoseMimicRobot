# PoseMimicRobot

[![Godot Engine](https://img.shields.io/badge/Godot-v4.x--.NET-blue?logo=godotengine&logoColor=white)](https://godotengine.org)
[![.NET](https://img.shields.io/badge/.NET-v8.0-purple?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/Language-C%23_12-green?logo=csharp&logoColor=white)](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![License](https://img.shields.io/badge/License-Apache2-yellow.svg)](LICENSE)

A physics-based Active Ragdoll Controller integrating Virtual Model Control (VMC), closed-loop Proportional-Derivative (PD) pose matching, and interactive ballistic debugging. Developed for Godot 4, this system utilizes first-principles rigid body dynamics and torque projection to maintain dynamic equilibrium without relying on kinematic pinning.

---

## Quick Start & Installation

### Prerequisites
* **Godot Engine v4.x** (specifically the **.NET edition**)
* **.NET 8.0 SDK** (or higher)

### Import Ragdoll Artifacts (only required for initial project setup)

```bash
godot --import
```

### Build & Run
Clone the repository and compile/run the C# solution:

```bash
# Build C# solution and execute in Godot
dotnet build && godot
```

---
# Technical Specification & Documentation

## 1. Subtree Mass & Gravity Compensation

The system recursively evaluates physical bone subtrees to calculate total mass and weighted global center of mass (CoM). Counter-torques are computed and applied to neutralize gravitational forces, rendering the structure dynamically weightless.

$$\boldsymbol{\tau}_{\mathrm{comp}} = (\mathbf{r}_{\mathrm{CoM, subtree}} -
\mathbf{r}_{\mathrm{pivot}}) \times (m_{\mathrm{subtree}} \mathbf{g})$$

```csharp
private void CompensateForGravity(float dt)
{
  foreach (var m in _muscles)
  {
    if (m.IsFinger || (RelaxArms && m.IsArm)) continue;

    Vector3 pivot = m.Bone.GlobalPosition;
    var (compositeMass, weightedPos) = GetSubtreeMassProperties(m);
    if (compositeMass <= 0.001f) continue;

    Vector3 compositeCOM = weightedPos / compositeMass;
    Vector3 leverArm = compositeCOM - pivot;
    Vector3 gravityForce = Vector3.Down * 9.8f * compositeMass;
    Vector3 counterTorque = leverArm.Cross(gravityForce);

    ApplyTorque(m.Bone, -counterTorque * GravityComp, dt, m.ParentBone);
  }
}
```

---

## 2. Quaternion Pose Matching (PD Control)

Joint target orientations are enforced using a PD control law. The system calculates the relative quaternion difference between the physical bone's global orientation and the target `AnimationShadow` orientation.

$$\Delta q = q_{\mathrm{target}} \cdot q_{\mathrm{current}}^{-1}$$

$$\boldsymbol{\tau}_{\mathrm{PD}} = (\hat{\mathbf{a}} \cdot \theta) \cdot K_p -
(\mathbf{\omega}_{\mathrm{bone}} - \mathbf{\omega}_{\mathrm{ref}}) \cdot K_d$$


```csharp
Quaternion currentQ = m.Bone.GlobalBasis.GetRotationQuaternion();
Quaternion targetQ = targetWorld.Basis.GetRotationQuaternion();
Quaternion diff = targetQ * currentQ.Inverse();
if (diff.W < 0f) diff = new Quaternion(-diff.X, -diff.Y, -diff.Z, -diff.W);

Vector3 axis = diff.GetAxis().Normalized();
float angle = diff.GetAngle();

Vector3 pTerm = (axis * angle) * stiffness;
Vector3 relativeVel = m.Bone.AngularVelocity - dampingRef;
Vector3 dTerm = relativeVel * damping;

Vector3 rawTorque = pTerm - dTerm;
ApplyTorque(m.Bone, rawTorque.LimitLength(limit), dt, null);
```

---

## 3. Virtual Model Control (VMC) & Balance

VMC evaluates a virtual operational force between the CoM and a weighted Center of Pressure (CoP). Height is regulated via a virtual spring-damper formulation.

$$F_y = \mathrm{Clamp}\left( (y_{\mathrm{target}} - y_{\mathrm{hip}}) \cdot
K_{\mathrm{support}} - v_y \cdot D_{\mathrm{support}}, \, 0, \, F_{\mathrm{max}} \right)$$


Ground contact evaluation functions determine physical confidence per leg chain. Operational space forces are converted to joint torques via Jacobian transpose ($J^T$) cross products.

$$\boldsymbol{\tau}_{\mathrm{joint}} = \mathbf{r}_{\mathrm{joint}
\rightarrow \mathrm{foot}} \times \mathbf{F}_{\mathrm{leg}}$$


```csharp
float heightError = TargetHeight - currentRelativeHeight;
float verticalForceMag = (heightError * SupportSpring) - (hipVel.Y * SupportDamp);
Vector3 desiredBodyForce = (Vector3.Up * verticalForceMag) + horizontalForce;
Vector3 footReactionForce = -desiredBodyForce.LimitLength(MaxForce);

// Torque Projection
Vector3 r_Hip = midFootPos - leg.UpperLeg.GlobalPosition;
Vector3 r_Knee = midFootPos - leg.LowerLeg.GlobalPosition;
Vector3 r_Ankle = midFootPos - leg.Foot.GlobalPosition;

ApplyTorque(leg.Foot, r_Ankle.Cross(legForce).LimitLength(MaxMuscleTorque), dt, leg.LowerLeg);
ApplyTorque(leg.LowerLeg, r_Knee.Cross(legForce).LimitLength(MaxMuscleTorque), dt, leg.UpperLeg);
ApplyTorque(leg.UpperLeg, r_Hip.Cross(legForce).LimitLength(MaxMuscleTorque), dt, _hips);
```

---

## 4. Core Orientation Stabilization

Hip orientation is explicitly stabilized by cross-product torque applications targeting specific up and forward vectors, minimizing angular deviation from the target animation trajectory.

```csharp
Vector3 rotAxis = currentUp.Cross(targetUp).Normalized();
float rotAngle = Mathf.Acos(Mathf.Clamp(currentUp.Dot(targetUp), -1f, 1f));

if (rotAngle > 0.001f) {
  totalTorque += rotAxis * (rotAngle * HipGyroStiffness);
}

Vector3 finalTorque = totalTorque - (_hips.AngularVelocity * HipGyroDamping);
ApplyTorque(_hips, finalTorque, dt);
```

---

## 5. System Interaction & Tooling

The implementation features an integrated FPS camera controller for real-time spatial manipulation and physical evaluation.

*   **Raycast Object Manipulation:** Utilizes a dynamically instantiated `Generic6DofJoint3D` constrained with explicit linear spring configurations (`linear_spring_*/stiffness`) to bind rigid bodies to the camera's local offset.
*   **Ballistic Evaluation Functions:**
    *   `SpinningProjectile`: Instantiates a sphere mesh with `FastNoiseLite` displacement, executing standard Newtonian momentum transfer.
    *   `AdogenProjectile`: Leverages continuous collision detection and custom shader permutations to apply concentrated directional forces (`AdogenBlastForce` = 20,000) directly to the root node structure.
*   **Aimed Translation:** Computes normalized directional vectors based on direct ray intersections to apply rapid linear central impulses (`AimedShoveForce` = 500) for stability testing.
