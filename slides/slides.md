---
theme: default
colorSchema: auto
# background: /images/cyberlit-wide-lite.png
title: Live Arch
titleTemplate: "%s - by Pavel Agarkov"
info: |
  ## Blueprints to Clouds: Turning Architecture into Automated Infrastructure
class: text-center
drawings:
  persist: false
transition: fade-out
mdc: true
duration: 60min
---

# Live Arch
### by Pavel Agarkov
<style>
.slidev-layout {
  background-image: url(/images/promo.png)!important;
}
h1 {
  color: white;
  letter-spacing: 0.3em;
  opacity: 0.8
}
h3 {
  color: white;
  opacity: 0.7;
  margin-right: 16px;
}
</style>

<div style="position:absolute;left:0;right:0;bottom:2rem;text-align:center;color:white" class="text-md opacity-90">
  Blueprints to Clouds: Turning Architecture into Automated Infrastructure
</div>

<div class="abs-br m-6 text-xl">
  <a href="https://github.com/pavel-agarkov/live-arch" target="_blank" class="slidev-icon-btn" style="color:white;">
    <carbon:logo-github />
  </a>
</div>

<!--
Hi, my name is Pavel Agarkov and I'm a Solution Architect at Capgemini.
I focus on Azure, .NET, event‑driven and distributed systems, high‑load workloads, and microservice architectures. Over the years, I’ve been involved in a wide range of projects, including cloud migrations, microservice decomposition, and platform automation. I enjoy designing systems that are both scalable and maintainable, and I’m passionate about improving engineering workflows through automation. At home, I’m a husband and a father to a one‑year‑old son — which means my main hobby right now is sleep management.
-->

---

# Intro

- this is a personal engineering story
- I am sharing a way I tried to solve real problems
- I am not trying to sell any specific technology
- I am not claiming this is the only right approach
- I hope parts of it may still be useful to others

<!--
Today I want to share my experience of solving a set of problems that were standing in front of me.

This talk is not a sales pitch for Structurizr, Pulumi, or for my own solution.
I am also not trying to claim that everyone should copy this approach exactly as it is.

What I want to do is much simpler.
I want to show:
- what problems I was trying to solve
- why the usual approaches were not enough for me
- what kind of solution I ended up building
- and which parts of that experience might still be useful for others

So the right way to listen to this talk is not: "should we adopt this exact stack tomorrow?"
The better question is: "is there anything in this approach that can help reduce the gap between architecture and delivery in our own context?"

That is the frame for the rest of the talk.
-->

---
src: ./sections/1.problem-space.md
---

---
src: ./sections/4.structurizr.md
---

---
src: ./sections/5.pulumi.md
---

---
src: ./sections/6.engine.md
---

---
src: ./sections/7.patterns.md
---

---
src: ./sections/8.run.md
---

---
src: ./sections/9.next.md
---

---

# Thank You!

<style>
.slidev-layout {
  text-align: center;
}
h1 {
  opacity: 0.8
}
h2 {
  opacity: 0.6
}
h3 {
  opacity: 0.5
}
h4 {
  opacity: 0.45
}
</style>

## Questions & Discussion
#### also online in [GitHub issue #1](https://github.com/pavel-agarkov/live-arch/issues/1)


<img src="/images/qr.png" style="display: inline; margin: 1rem;" width="200px">

### Link to the presentation

<br/>

<PoweredBySlidev mt-10 opacity-50 />

<!--
  presenter comments
-->
