---
theme: default
# colorSchema: dark
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
  background-image: url(/images/cyberlit-wide-lite.png)!important;
}
h1 {
  letter-spacing: 0.3em;
  opacity: 0.6
}
h3 {
  opacity: 0.5
}
</style>

<div style="position:absolute;left:0;right:0;bottom:2rem;text-align:center" class="text-md opacity-50">
  Blueprints to Clouds: Turning Architecture into Automated Infrastructure
</div>

<div class="abs-br m-6 text-xl">
  <a href="https://github.com/pavel-agarkov/live-arch" target="_blank" class="slidev-icon-btn">
    <carbon:logo-github />
  </a>
</div>

<!--
  presenter comments
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
src: ./sections/1.problem.md
---

---
src: ./sections/2.why.md
---

---
src: ./sections/3.goals.md
---

---
src: ./sections/4.structurizr.md
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
#### also online in [GitHub issue #1](https://github.com/pavel-agarkov/tech-scam/issues/1)


<img src="/images/qr.png" style="display: inline; margin: 1rem;" width="200px">

### Link to the presentation

<br/>

<PoweredBySlidev mt-10 opacity-50 />

<!--
  presenter comments
-->
