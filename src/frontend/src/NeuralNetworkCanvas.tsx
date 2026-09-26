import { useEffect, useRef } from 'react'

interface NetworkNode {
  baseX: number
  baseY: number
  x: number
  y: number
  vx: number
  vy: number
  dispX: number
  dispY: number
  radius: number
  color: string
}

export function NeuralNetworkCanvas() {
  const canvasRef = useRef<HTMLCanvasElement | null>(null)

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return

    // In jsdom test environment without canvas package, exit gracefully
    if (typeof navigator !== 'undefined' && navigator.userAgent && navigator.userAgent.includes('jsdom')) {
      return
    }

    const ctx = canvas.getContext('2d', { alpha: true })
    if (!ctx) return

    let animationFrameId: number
    let width = 0
    let height = 0

    // Pointer state kept completely outside React lifecycle
    const pointer = {
      x: -9999,
      y: -9999,
      active: false,
      lastMoved: 0,
    }

    let nodes: NetworkNode[] = []

    // High contrast palette visible against baby-blue #edf5fa
    const palette = [
      'rgba(5, 51, 94, 0.85)',    // CineKros deep navy (high contrast)
      'rgba(10, 155, 170, 0.90)', // CineKros rich teal
      'rgba(5, 42, 83, 0.95)',    // CineKros dark ocean
      'rgba(255, 255, 255, 0.95)', // Crisp white node
    ]

    const initNodes = () => {
      let targetCount: number
      if (width >= 1200) {
        targetCount = Math.min(185, Math.max(145, Math.floor((width * height) / 9500)))
      } else if (width >= 600) {
        targetCount = Math.min(115, Math.max(85, Math.floor((width * height) / 9000)))
      } else {
        targetCount = Math.min(65, Math.max(45, Math.floor((width * height) / 7500)))
      }

      nodes = Array.from({ length: targetCount }, () => {
        const speed = 0.12 + Math.random() * 0.16
        const angle = Math.random() * Math.PI * 2
        const vx = Math.cos(angle) * speed
        const vy = Math.sin(angle) * speed
        const startX = Math.random() * width
        const startY = Math.random() * height
        return {
          baseX: startX,
          baseY: startY,
          x: startX,
          y: startY,
          vx,
          vy,
          dispX: 0,
          dispY: 0,
          radius: 2.0 + Math.random() * 1.8,
          color: palette[Math.floor(Math.random() * palette.length)],
        }
      })
    }

    const resize = () => {
      if (!canvas) return
      width = window.innerWidth
      height = window.innerHeight
      const dpr = Math.min(window.devicePixelRatio || 1, 2)
      canvas.width = Math.floor(width * dpr)
      canvas.height = Math.floor(height * dpr)
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
      initNodes()
    }

    resize()
    window.addEventListener('resize', resize, { passive: true })

    const updatePointerPos = (clientX: number, clientY: number) => {
      pointer.x = clientX
      pointer.y = clientY
      pointer.active = true
      pointer.lastMoved = performance.now()
    }

    const onPointerMove = (e: MouseEvent | PointerEvent) => {
      updatePointerPos(e.clientX, e.clientY)
    }

    const onTouchMove = (e: TouchEvent) => {
      if (e.touches && e.touches[0]) {
        updatePointerPos(e.touches[0].clientX, e.touches[0].clientY)
      }
    }

    const onPointerLeave = () => {
      pointer.active = false
      pointer.x = -9999
      pointer.y = -9999
    }

    window.addEventListener('pointermove', onPointerMove, { passive: true })
    window.addEventListener('mousemove', onPointerMove, { passive: true })
    window.addEventListener('touchmove', onTouchMove, { passive: true })
    window.addEventListener('pointerdown', onPointerMove, { passive: true })
    document.addEventListener('pointermove', onPointerMove, { passive: true })
    document.addEventListener('mousemove', onPointerMove, { passive: true })
    window.addEventListener('pointerleave', onPointerLeave, { passive: true })
    window.addEventListener('mouseout', onPointerLeave, { passive: true })
    window.addEventListener('blur', onPointerLeave)

    const render = (time: number) => {
      ctx.clearRect(0, 0, width, height)

      const len = nodes.length
      const maxConnDist = width < 600 ? 100 : width < 1200 ? 125 : 145
      const pointerRadius = 240
      const pointerLineDist = 200

      const isPointerActive = pointer.active && (time - pointer.lastMoved < 4000)

      // 1. Update node physics: autonomous drift + fluid cursor wave repulsion
      for (let i = 0; i < len; i++) {
        const node = nodes[i]

        // Drift
        node.baseX += node.vx
        node.baseY += node.vy

        // Wrap viewport edges
        if (node.baseX < -20) node.baseX = width + 20
        else if (node.baseX > width + 20) node.baseX = -20
        if (node.baseY < -20) node.baseY = height + 20
        else if (node.baseY > height + 20) node.baseY = -20

        if (isPointerActive) {
          const pdx = node.baseX - pointer.x
          const pdy = node.baseY - pointer.y
          const pDist = Math.hypot(pdx, pdy)

          if (pDist < pointerRadius && pDist > 0.5) {
            // Distinct quadratic repulsion wave pushing nodes away up to 85px
            const force = Math.pow(1 - pDist / pointerRadius, 1.4)
            const targetDispX = (pdx / pDist) * force * 85
            const targetDispY = (pdy / pDist) * force * 85
            node.dispX += (targetDispX - node.dispX) * 0.22
            node.dispY += (targetDispY - node.dispY) * 0.22
          } else {
            // Elastic smooth return toward baseline position
            node.dispX += (0 - node.dispX) * 0.08
            node.dispY += (0 - node.dispY) * 0.08
          }
        } else {
          node.dispX += (0 - node.dispX) * 0.08
          node.dispY += (0 - node.dispY) * 0.08
        }

        node.x = node.baseX + node.dispX
        node.y = node.baseY + node.dispY
      }

      // 2. Draw connections between nearby nodes with high contrast navy/teal
      for (let i = 0; i < len; i++) {
        const n1 = nodes[i]
        for (let j = i + 1; j < len; j++) {
          const n2 = nodes[j]
          const dx = n1.x - n2.x
          const dy = n1.y - n2.y
          const dist = Math.hypot(dx, dy)

          if (dist < maxConnDist) {
            const alpha = (1 - dist / maxConnDist) * 0.38
            ctx.strokeStyle = `rgba(5, 51, 94, ${alpha.toFixed(3)})`
            ctx.lineWidth = 1.0
            ctx.beginPath()
            ctx.moveTo(n1.x, n1.y)
            ctx.lineTo(n2.x, n2.y)
            ctx.stroke()
          }
        }
      }

      // 3. Draw active cursor spider-web lines directly to nearby nodes
      if (isPointerActive) {
        for (let i = 0; i < len; i++) {
          const node = nodes[i]
          const pdx = node.x - pointer.x
          const pdy = node.y - pointer.y
          const pDist = Math.hypot(pdx, pdy)

          if (pDist < pointerLineDist) {
            const pAlpha = (1 - pDist / pointerLineDist) * 0.65
            ctx.strokeStyle = `rgba(10, 155, 170, ${pAlpha.toFixed(3)})`
            ctx.lineWidth = 1.4
            ctx.beginPath()
            ctx.moveTo(pointer.x, pointer.y)
            ctx.lineTo(node.x, node.y)
            ctx.stroke()
          }
        }

        // Visible interactive cursor aura
        ctx.strokeStyle = 'rgba(10, 155, 170, 0.45)'
        ctx.lineWidth = 1.8
        ctx.beginPath()
        ctx.arc(pointer.x, pointer.y, 22, 0, Math.PI * 2)
        ctx.stroke()

        ctx.fillStyle = 'rgba(5, 51, 94, 0.75)'
        ctx.beginPath()
        ctx.arc(pointer.x, pointer.y, 4, 0, Math.PI * 2)
        ctx.fill()
      }

      // 4. Draw all nodes with crisp definition
      for (let i = 0; i < len; i++) {
        const node = nodes[i]
        ctx.fillStyle = node.color
        ctx.beginPath()
        ctx.arc(node.x, node.y, node.radius, 0, Math.PI * 2)
        ctx.fill()
      }

      animationFrameId = requestAnimationFrame(render)
    }

    animationFrameId = requestAnimationFrame(render)

    return () => {
      cancelAnimationFrame(animationFrameId)
      window.removeEventListener('resize', resize)
      window.removeEventListener('pointermove', onPointerMove)
      window.removeEventListener('mousemove', onPointerMove)
      window.removeEventListener('touchmove', onTouchMove)
      window.removeEventListener('pointerdown', onPointerMove)
      document.removeEventListener('pointermove', onPointerMove)
      document.removeEventListener('mousemove', onPointerMove)
      window.removeEventListener('pointerleave', onPointerLeave)
      window.removeEventListener('mouseout', onPointerLeave)
      window.removeEventListener('blur', onPointerLeave)
    }
  }, [])

  return (
    <canvas
      ref={canvasRef}
      className="neural-canvas"
      aria-hidden="true"
    />
  )
}
